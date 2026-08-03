namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    internal static async Task<int> RunAllAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("creates and renames a project", Sync(CreatesAndRenamesProject)),
            ("adds edits and deletes environments", Sync(ManagesEnvironments)),
            ("rejects duplicate environment names", Sync(RejectsDuplicateEnvironmentNames)),
            ("creates nested groups endpoints and cases", Sync(CreatesNestedWorkspace)),
            ("rejects duplicate sibling names", Sync(RejectsDuplicateSiblingNames)),
            ("deleting a selected branch repairs selection", Sync(DeletingBranchRepairsSelection)),
            ("joins base URL and route", Sync(JoinsBaseUrlAndRoute)),
            ("encodes enabled query parameters", Sync(EncodesQueryParameters)),
            ("reports URL source information", Sync(ReportsUrlSources)),
            ("preserves route query string", Sync(PreservesRouteQueryString)),
            ("composes project and nested group route parts", Sync(ComposesNestedRouteParts)),
            ("ignores empty optional route prefixes", Sync(IgnoresOptionalRoutePrefixes)),
            ("encodes path parameters and query values", Sync(EncodesPathParameters)),
            ("reports missing path parameters", Sync(ReportsMissingPathParameters)),
            ("accepts a valid request definition", Sync(AcceptsValidRequest)),
            ("rejects invalid base URL and route", Sync(RejectsInvalidUrlParts)),
            ("rejects invalid JSON with a location", Sync(RejectsInvalidJson)),
            ("formats valid JSON", Sync(FormatsJson)),
            ("rejects duplicate enabled headers", Sync(RejectsDuplicateHeaders)),
            ("rejects enabled fields without names", Sync(RejectsNamelessEnabledFields)),
            ("tracks request and field edits as unsaved", Sync(TracksUnsavedChanges)),
            ("tracks route configuration as unsaved", Sync(TracksRouteChanges)),
            ("inherits and overrides variables with source information", ResolvesVariableInheritance),
            ("detects missing and circular variables", DetectsVariableFailures),
            ("keeps secrets out of project JSON and isolates secure values", ProtectsSecrets),
            ("retries secure storage loading after a failure", RetriesSecureStorageLoadAfterFailure),
            ("preserves secure storage state after save failures", PreservesSecureStorageStateAfterSaveFailures),
            ("prepares substituted requests and structured authentication", PreparesVariablesAndAuthentication),
            ("injects bearer and basic authentication", InjectsBearerAndBasicAuthentication),
            ("formats and inserts variable references", Sync(InsertsVariableReferences)),
            ("assists JSON pairing indentation and completion", Sync(AssistsJsonEditing)),
            ("tracks JSON editor undo and redo states", Sync(TracksJsonEditorHistory)),
            ("builds ordered route composition inspection parts", BuildsRouteCompositionInspection),
            ("keeps inspector variables masked and request-focused", BuildsInspectorVariableSummary),
            ("summarizes authentication without token values", BuildsInspectorAuthenticationSummary),
            ("normalizes responsive inspector layout state", Sync(ManagesInspectorLayoutState)),
            ("extracts tokens and evaluates expiry", Sync(ExtractsTokens)),
            ("refreshes tokens atomically and preserves failures", RefreshesTokens),
            ("coordinates token session persistence and refresh history", CoordinatesTokenSessions),
            ("orchestrates refresh execution history and token capture", OrchestratesRequestExecution),
            ("coordinates project lifecycle transitions and recent restore", CoordinatesProjectLifecycle),
            ("preserves completed project saves when recent state persistence fails", PreservesCompletedProjectSave),
            ("saves and reloads a complete project", SavesAndReloadsProject),
            ("loads version one projects without changing URLs", LoadsVersionOneProject),
            ("rejects corrupted project JSON", RejectsCorruptedProject),
            ("rejects unsupported project versions", RejectsUnsupportedVersion),
            ("rejects invalid workspace hierarchy", RejectsInvalidHierarchy),
            ("repairs stale saved selections", RepairsStaleSelections),
            ("builds and executes an HTTP request", ExecutesHttpRequest),
            ("builds a masked final request snapshot and PowerShell curl", BuildsSafeRequestSnapshot),
            ("records redirects and rewrites redirect methods", RecordsRedirectChain),
            ("detects redirect loops and redirect limits", DetectsRedirectFailures),
            ("removes sensitive headers on cross-host redirects", ProtectsCrossHostRedirects),
            ("formats JSON text HTML binary and empty responses", Sync(FormatsResponseKinds)),
            ("truncates oversized response bodies", TruncatesLargeResponse),
            ("classifies TLS and connection failures", ClassifiesNetworkFailures),
            ("distinguishes timeout and user cancellation", DistinguishesTimeoutAndCancellation),
            ("probes DNS and TCP without public services", ProbesDnsAndTcp),
            ("extracts TLS certificate diagnostics", Sync(ExtractsTlsCertificateDiagnostics)),
            ("maps diagnostics to factual suggestions", Sync(MapsDiagnosticSuggestions)),
            ("cancels diagnostics and isolates results by request", CancelsAndIsolatesDiagnostics),
            ("keeps bounded request history by request", Sync(ManagesRequestHistory)),
            ("masks sensitive JSON and attributes only injected API key headers", MasksSensitiveRequestData),
            ("does not replay unsafe requests during diagnostics", AvoidsUnsafeDiagnosticReplay),
            ("reorders route blocks and query parameters", Sync(ReordersRouteBlocks)),
            ("builds one shared route catalog and detects conflicts", BuildsRouteCatalog),
            ("debounces superseded route catalog refreshes", DebouncesRouteCatalogRefreshes),
            ("cancels stale route catalog builds and invalidates revisions", CancelsStaleRouteCatalogBuilds),
            ("generates stable masked Markdown documentation", GeneratesMarkdownDocumentation),
            ("round trips version four documentation metadata", RoundTripsDocumentationMetadata),
            ("keeps URL catalog and documentation aligned after reorder", ReorderKeepsRepresentationsAligned),
            ("migrates version two and three documentation defaults", MigratesLegacyDocumentationDefaults),
            ("builds a large route catalog within a bounded time", BuildsLargeRouteCatalog)
        };

        var failures = new List<string>();

        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{test.Name}: {exception.Message}");
                Console.WriteLine($"FAIL  {test.Name}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine(failure);
            }

            return 1;
        }

        return 0;
    }
}
