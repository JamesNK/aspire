// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test that verifies telemetry enrichment tags are present on spans
/// exported via OTLP to the standalone dashboard.
/// </summary>
public sealed class CliTelemetryTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task OtlpExportedSpansContainEnrichmentTags()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Store the dashboard log path inside the workspace so it gets captured on failure
        var dashboardLogPath = $"/workspace/{workspace.WorkspaceRoot.Name}/dashboard.log";

        // Start the dashboard in the background with anonymous access (no auth needed)
        // and default OTLP gRPC endpoint on port 4317.
        await auto.TypeAsync($"aspire dashboard run --allow-anonymous > {dashboardLogPath} 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Store the dashboard PID for cleanup
        await auto.TypeAsync("DASHBOARD_PID=$!");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the dashboard to become ready by polling the frontend URL
        await auto.TypeAsync("for i in $(seq 1 30); do curl -ksSL -o /dev/null -w '%{http_code}' http://localhost:18888 2>/dev/null | grep -q 200 && break; sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Configure OTLP export to point to the dashboard's OTLP gRPC endpoint.
        // ASPIRE_PROFILING_ENABLED is required in Release builds to activate the OTLP exporter.
        await auto.TypeAsync("export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("export ASPIRE_PROFILING_ENABLED=true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run 'aspire new' to generate diagnostic telemetry that is exported via OTLP
        // to the dashboard. The CliExportProcessor adds enrichment tags before export.
        await auto.AspireNewAsync("TelemetryTestApp", counter);

        // Allow time for the batch exporter to flush spans to the dashboard
        await auto.TypeAsync("sleep 5");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Query spans from the dashboard in JSON format
        await auto.TypeAsync("aspire otel spans --format json --dashboard-url http://localhost:18888 > spans.json 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Dump spans for debugging visibility in the recording
        await auto.TypeAsync("echo '=== SPANS JSON ==='; cat spans.json; echo '=== END SPANS JSON ==='");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert that spans were received (not empty array)
        await auto.TypeAsync("jq -e 'length > 0' spans.json && echo 'SPANS_RECEIVED' || echo 'NO_SPANS'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SPANS_RECEIVED", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert enrichment tags are present on the exported spans.
        // The CliExportProcessor adds these tags at export time from TelemetryTagsSource.
        // Check that at least one span has the aspire.cli.version attribute set.
        await auto.TypeAsync("jq -e '[.[].attributes[\"aspire.cli.version\"] // empty] | length > 0' spans.json && echo 'HAS_CLI_VERSION' || echo 'MISSING_CLI_VERSION'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("HAS_CLI_VERSION", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: kill the background dashboard process
        await auto.TypeAsync("kill -9 $DASHBOARD_PID 2>/dev/null; wait $DASHBOARD_PID 2>/dev/null; true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
