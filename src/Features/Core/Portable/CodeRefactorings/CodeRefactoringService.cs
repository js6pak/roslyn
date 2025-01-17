﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixesAndRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Telemetry;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeRefactorings;

[Export(typeof(ICodeRefactoringService)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CodeRefactoringService(
    [ImportMany] IEnumerable<Lazy<CodeRefactoringProvider, CodeChangeProviderMetadata>> providers) : ICodeRefactoringService
{
    private readonly Lazy<ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>>> _lazyLanguageToProvidersMap = new Lazy<ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>>>(
            () =>
                ImmutableDictionary.CreateRange(
                    DistributeLanguages(providers)
                        .GroupBy(lz => lz.Metadata.Language)
                        .Select(grp => KeyValuePairUtil.Create(
                            grp.Key,
                            new Lazy<ImmutableArray<CodeRefactoringProvider>>(() => ExtensionOrderer.Order(grp).Select(lz => lz.Value).ToImmutableArray())))));
    private readonly Lazy<ImmutableDictionary<CodeRefactoringProvider, CodeChangeProviderMetadata>> _lazyRefactoringToMetadataMap = new(() => providers.Where(provider => provider.IsValueCreated).ToImmutableDictionary(provider => provider.Value, provider => provider.Metadata));

    private ImmutableDictionary<CodeRefactoringProvider, FixAllProviderInfo?> _fixAllProviderMap = ImmutableDictionary<CodeRefactoringProvider, FixAllProviderInfo?>.Empty;

    private static IEnumerable<Lazy<CodeRefactoringProvider, OrderableLanguageMetadata>> DistributeLanguages(IEnumerable<Lazy<CodeRefactoringProvider, CodeChangeProviderMetadata>> providers)
    {
        foreach (var provider in providers)
        {
            foreach (var language in provider.Metadata.Languages)
            {
                var orderable = new OrderableLanguageMetadata(
                    provider.Metadata.Name, language, provider.Metadata.AfterTyped, provider.Metadata.BeforeTyped);
                yield return new Lazy<CodeRefactoringProvider, OrderableLanguageMetadata>(() => provider.Value, orderable);
            }
        }
    }

    private ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>> LanguageToProvidersMap
        => _lazyLanguageToProvidersMap.Value;

    private ImmutableDictionary<CodeRefactoringProvider, CodeChangeProviderMetadata> RefactoringToMetadataMap
        => _lazyRefactoringToMetadataMap.Value;

    private ConcatImmutableArray<CodeRefactoringProvider> GetProviders(TextDocument document)
    {
        var allRefactorings = ImmutableArray<CodeRefactoringProvider>.Empty;
        if (LanguageToProvidersMap.TryGetValue(document.Project.Language, out var lazyProviders))
        {
            allRefactorings = ProjectCodeRefactoringProvider.FilterExtensions(document, lazyProviders.Value, GetExtensionInfo);
        }

        return allRefactorings.ConcatFast(GetProjectRefactorings(document));

        static ImmutableArray<CodeRefactoringProvider> GetProjectRefactorings(TextDocument document)
        {
            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict refactorings in Interactive
            if (document.Project.Solution.WorkspaceKind == WorkspaceKind.Interactive)
                return [];

            return ProjectCodeRefactoringProvider.GetExtensions(document, GetExtensionInfo);
        }

        static ProjectCodeRefactoringProvider.ExtensionInfo GetExtensionInfo(ExportCodeRefactoringProviderAttribute attribute)
            => new(attribute.DocumentKinds, attribute.DocumentExtensions);
    }

    public async Task<bool> HasRefactoringsAsync(
        TextDocument document,
        TextSpan state,
        CodeActionOptionsProvider options,
        CancellationToken cancellationToken)
    {
        foreach (var provider in GetProviders(document))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var refactoring = await GetRefactoringFromProviderAsync(
                document, state, provider, options, cancellationToken).ConfigureAwait(false);

            if (refactoring != null)
                return true;
        }

        return false;
    }

    public async Task<ImmutableArray<CodeRefactoring>> GetRefactoringsAsync(
        TextDocument document,
        TextSpan state,
        CodeActionRequestPriority? priority,
        CodeActionOptionsProvider options,
        Func<string, IDisposable?> addOperationScope,
        CancellationToken cancellationToken)
    {
        using (TelemetryLogging.LogBlockTimeAggregated(FunctionId.CodeRefactoring_Summary, $"Pri{priority.GetPriorityInt()}"))
        using (Logger.LogBlock(FunctionId.Refactoring_CodeRefactoringService_GetRefactoringsAsync, cancellationToken))
        {
            using var _1 = ArrayBuilder<(CodeRefactoringProvider provider, CodeRefactoring codeRefactoring)>.GetInstance(out var pairs);
            using var _2 = PooledDictionary<CodeRefactoringProvider, int>.GetInstance(out var providerToIndex);

            var orderedProviders = GetProviders(document).Where(p => priority == null || p.RequestPriority == priority).ToImmutableArray();
            foreach (var provider in orderedProviders)
                providerToIndex.Add(provider, providerToIndex.Count);

            await ProducerConsumer<(CodeRefactoringProvider provider, CodeRefactoring codeRefactoring)>.RunParallelAsync(
                source: orderedProviders,
                produceItems: static async (provider, callback, args, cancellationToken) =>
                {
                    // Run all providers in parallel to get the set of refactorings for this document.
                    // Log an individual telemetry event for slow code refactoring computations to
                    // allow targeted trace notifications for further investigation. 500 ms seemed like
                    // a good value so as to not be too noisy, but if fired, indicates a potential
                    // area requiring investigation.
                    const int CodeRefactoringTelemetryDelay = 500;

                    var providerName = provider.GetType().Name;

                    var logMessage = KeyValueLogMessage.Create(m =>
                    {
                        m[TelemetryLogging.KeyName] = providerName;
                        m[TelemetryLogging.KeyLanguageName] = args.document.Project.Language;
                    });

                    using (args.addOperationScope(providerName))
                    using (RoslynEventSource.LogInformationalBlock(FunctionId.Refactoring_CodeRefactoringService_GetRefactoringsAsync, providerName, cancellationToken))
                    using (TelemetryLogging.LogBlockTime(FunctionId.CodeRefactoring_Delay, logMessage, CodeRefactoringTelemetryDelay))
                    {
                        var refactoring = await args.@this.GetRefactoringFromProviderAsync(
                            args.document, args.state, provider, args.options, cancellationToken).ConfigureAwait(false);
                        if (refactoring != null)
                            callback((provider, refactoring));
                    }
                },
                consumeItems: static async (reader, args, cancellationToken) =>
                {
                    await foreach (var pair in reader)
                        args.pairs.Add(pair);
                },
                args: (@this: this, document, state, options, addOperationScope, pairs),
                cancellationToken).ConfigureAwait(false);

            return pairs
                .OrderBy((tuple1, tuple2) => providerToIndex[tuple1.provider] - providerToIndex[tuple2.provider])
                .SelectAsArray(t => t.codeRefactoring);
        }
    }

    private Task<CodeRefactoring?> GetRefactoringFromProviderAsync(
        TextDocument textDocument,
        TextSpan state,
        CodeRefactoringProvider provider,
        CodeActionOptionsProvider options,
        CancellationToken cancellationToken)
    {
        RefactoringToMetadataMap.TryGetValue(provider, out var providerMetadata);

        var extensionManager = textDocument.Project.Solution.Services.GetRequiredService<IExtensionManager>();

        return extensionManager.PerformFunctionAsync(
            provider,
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var _ = ArrayBuilder<(CodeAction action, TextSpan? applicableToSpan)>.GetInstance(out var actions);
                var context = new CodeRefactoringContext(textDocument, state,

                    // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                    (action, applicableToSpan) =>
                    {
                        // Serialize access for thread safety - we don't know what thread the refactoring provider will call this delegate from.
                        lock (actions)
                        {
                            // Add the Refactoring Provider Name to the parent CodeAction's CustomTags.
                            // Always add a name even in cases of 3rd party refactorings that do not export
                            // name metadata.
                            action.AddCustomTagAndTelemetryInfo(providerMetadata, provider);

                            actions.Add((action, applicableToSpan));
                        }
                    },
                    options,
                    cancellationToken);

                var task = provider.ComputeRefactoringsAsync(context) ?? Task.CompletedTask;
                await task.ConfigureAwait(false);

                if (actions.Count == 0)
                {
                    return null;
                }

                var fixAllProviderInfo = extensionManager.PerformFunction(
                    provider, () => ImmutableInterlocked.GetOrAdd(ref _fixAllProviderMap, provider, FixAllProviderInfo.Create), defaultValue: null);
                return new CodeRefactoring(provider, actions.ToImmutable(), fixAllProviderInfo, options);
            }, defaultValue: null, cancellationToken);
    }

    private class ProjectCodeRefactoringProvider
        : AbstractProjectExtensionProvider<ProjectCodeRefactoringProvider, CodeRefactoringProvider, ExportCodeRefactoringProviderAttribute>
    {
        protected override ImmutableArray<string> GetLanguages(ExportCodeRefactoringProviderAttribute exportAttribute)
            => [.. exportAttribute.Languages];

        protected override bool TryGetExtensionsFromReference(AnalyzerReference reference, out ImmutableArray<CodeRefactoringProvider> extensions)
        {
            // check whether the analyzer reference knows how to return fixers directly.
            if (reference is ICodeRefactoringProviderFactory codeRefactoringProviderFactory)
            {
                extensions = codeRefactoringProviderFactory.GetRefactorings();
                return true;
            }

            extensions = default;
            return false;
        }
    }
}
