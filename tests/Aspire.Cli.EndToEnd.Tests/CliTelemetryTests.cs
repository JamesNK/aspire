// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test that verifies telemetry enrichment tags are present on spans
/// exported via OTLP to the standalone dashboard. Uses <c>aspire start</c> to
/// generate profiling telemetry which is the only source exported via OTLP in
/// Release builds.
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

        // Docker socket needed because aspire start runs an AppHost that may launch containers
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Start the dashboard in the background with anonymous access.
        // The dashboard's default OTLP gRPC endpoint listens on port 4317.
        var dashboardLogPath = $"/workspace/{workspace.WorkspaceRoot.Name}/dashboard.log";
        await auto.TypeAsync($"aspire dashboard run --allow-anonymous > {dashboardLogPath} 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("DASHBOARD_PID=$!");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the dashboard to become ready
        await auto.TypeAsync("for i in $(seq 1 30); do curl -ksSL -o /dev/null -w '%{http_code}' http://localhost:18888 2>/dev/null | grep -q 200 && break; sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Configure OTLP export to the dashboard's gRPC endpoint.
        // ASPIRE_PROFILING_ENABLED activates the profiling TracerProvider which exports via OTLP.
        await auto.TypeAsync("export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("export ASPIRE_PROFILING_ENABLED=true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Create a project to start
        await auto.AspireNewAsync("TelemetryTestApp", counter);

        // Navigate to the AppHost
        await auto.TypeAsync("cd TelemetryTestApp/TelemetryTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost — this generates profiling spans exported via OTLP.
        // The CliTagEnrichmentProcessor enriches these spans with default tags before export.
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(3));

        // Give the batch exporter time to flush to the dashboard, then stop the AppHost.
        await auto.TypeAsync("sleep 3");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStopAsync(counter);

        // Poll for spans from the dashboard with retries.
        // Write result to a file so we can check it in a separate command (avoids
        // WaitUntilTextAsync matching typed command text on the terminal screen).
        await auto.TypeAsync("for attempt in $(seq 1 10); do aspire otel spans --format json --dashboard-url http://localhost:18888 > spans.json 2>&1; if jq -e 'length > 0' spans.json >/dev/null 2>&1; then echo PASS > /tmp/spans_result; break; fi; sleep 2; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert spans were received. Clear screen first to avoid matching stale text.
        await auto.TypeAsync("clear");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("cat /tmp/spans_result");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("PASS", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Dump spans for debugging visibility in the recording
        await auto.TypeAsync("cat spans.json | head -100");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert enrichment tags are present on the exported spans.
        // The CliTagEnrichmentProcessor adds aspire.cli.version from TelemetryTagsSource.
        await auto.TypeAsync("jq -e '[.[].attributes[\"aspire.cli.version\"] // empty] | length > 0' spans.json >/dev/null 2>&1 && echo PASS > /tmp/ver_result || echo FAIL > /tmp/ver_result");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear and check version result
        await auto.TypeAsync("clear");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("cat /tmp/ver_result");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("PASS", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: kill the background dashboard process
        await auto.TypeAsync("kill -9 $DASHBOARD_PID 2>/dev/null; wait $DASHBOARD_PID 2>/dev/null; true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
