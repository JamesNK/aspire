// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Aspire.Hosting;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Telemetry;

// This file is the CLI's OpenTelemetry wiring point. It decides which ActivitySources
// are listened to and where they are exported; the activity creation APIs live in
// AspireCliTelemetry and ProfilingTelemetry.
//
// A single TracerProvider listens to all enabled activity sources, and filtering export
// processors route activities to the correct exporter based on source name. Reported
// telemetry is allowed to leave the machine through Azure Monitor, while profiling and
// diagnostic telemetry are intentionally local and opt-in because they can include
// high-cardinality process, path, and startup timing details.
//
// Enablement is intentionally separate:
// - Reported telemetry is on by default and is disabled with ASPIRE_CLI_TELEMETRY_OPTOUT=true.
// - Profiling telemetry requires ASPIRE_PROFILING_ENABLED=true plus OTEL_EXPORTER_OTLP_ENDPOINT
//   (and typically OTEL_EXPORTER_OTLP_PROTOCOL=grpc). ASPIRE_STARTUP_PROFILING_ENABLED is the
//   legacy alias that remains supported for existing scripts.
// - DEBUG-only diagnostics use ASPIRE_CLI_CONSOLE_EXPORTER_LEVEL=Diagnostic, or OTLP export when
//   OTEL_EXPORTER_OTLP_ENDPOINT is set. When profiling is also enabled, profiling and diagnostic
//   activities share a single OTLP exporter.

/// <summary>
/// Manages a single OpenTelemetry <see cref="TracerProvider"/> for the CLI.
/// Uses <see cref="FilteringExportProcessor"/> to route activities from different sources
/// to the appropriate exporter (Azure Monitor, OTLP profiling, or debug diagnostics).
/// </summary>
internal sealed class TelemetryManager : IDisposable
{
    // Remote export connection string for Application Insights. Intentionally hard-coded.
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=e39510fc-95a1-423d-9f33-6121bf0d2113;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=4d8bb9db-b7ab-49f9-978b-80ae1e83f6da";

#if DEBUG
    // No timeout in debug builds
    private const int ShutDownTimeoutMilliseconds = -1;
#else
    // Chosen to provide time to send remaining telemetry without noticeably delaying exit.
    private const int ShutDownTimeoutMilliseconds = 200;
#endif
    private const int ProfilingForceFlushTimeoutMilliseconds = 5000;

    private readonly TracerProvider? _provider;

    // Kept for targeted profiling flush without flushing all exporters.
    private readonly FilteringExportProcessor? _profilingProcessor;

    private readonly bool _hasAzureMonitor;
    private readonly bool _hasProfilingProvider;
    private readonly bool _hasDiagnosticProvider;
    private bool _shuttingDown;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="configuration">The configuration to read telemetry settings from.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    /// <param name="args">The command-line arguments.</param>
    public TelemetryManager(IConfiguration configuration, TelemetryTagsSource tagsSource, string[]? args = null)
    {
        // Don't send telemetry for informational commands or if the user has opted out.
        var hasOptOutArg = args?.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a)) ?? false;
        var telemetryOptOut = hasOptOutArg || configuration.GetBool(AspireCliTelemetry.TelemetryOptOutConfigKey, defaultValue: false);

        var profilingEnabled =
            configuration.GetBool(KnownConfigNames.ProfilingEnabled) ??
            configuration.GetBool(KnownConfigNames.Legacy.StartupProfilingEnabled, defaultValue: false);
        var requestedOtlpExporter = !string.IsNullOrEmpty(configuration[AspireCliTelemetry.OtlpExporterEndpointConfigKey]);
        var useProfilingExporter = profilingEnabled && requestedOtlpExporter;

#if DEBUG
        var consoleExporterLevel = configuration.GetEnum<ConsoleExporterLevel>(AspireCliTelemetry.ConsoleExporterLevelConfigKey, defaultValue: null);
#else
        ConsoleExporterLevel? consoleExporterLevel = null;
#endif
        // The OTLP exporter is shared between profiling and diagnostic activities. It is
        // enabled when the OTLP endpoint is set and either profiling is explicitly opted in
        // or (DEBUG-only) the endpoint alone is enough to activate diagnostics.
        var useOtlpExporter = requestedOtlpExporter && (profilingEnabled
#if DEBUG
            || true
#endif
            );
        var useDiagnosticConsoleExporter = consoleExporterLevel == ConsoleExporterLevel.Diagnostic;
        var useAzureMonitor = !telemetryOptOut;

        // Don't create the provider if nothing is enabled.
        if (!useAzureMonitor && !useOtlpExporter && !useDiagnosticConsoleExporter)
        {
            return;
        }

        var resource = ResourceBuilder.CreateDefault().AddService(
            serviceName: "aspire-cli",
            // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
            // the OTel service version identifies the actual running binary that produced the
            // telemetry, so it must NOT be replaced by an emulated ASPIRE_CLI_VERSION identity.
            // The emulated identity is emitted separately as identity.* tags (AspireCliTelemetry).
            serviceVersion: VersionHelper.GetDefaultTemplateVersion());

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource);

        // Subscribe to each activity source that has at least one enabled exporter.
        if (useAzureMonitor)
        {
            builder.AddSource(AspireCliTelemetry.ReportedActivitySourceName);
        }

        if (useOtlpExporter || useDiagnosticConsoleExporter)
        {
            builder.AddSource(ProfilingTelemetry.ActivitySourceName);
            builder.AddSource(AspireCliTelemetry.DiagnosticsActivitySourceName);
        }

        // Azure Monitor exporter: only receives activities from the Reported source.
        if (useAzureMonitor)
        {
            var azureMonitorExporter = new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
            {
                ConnectionString = ApplicationInsightsConnectionString,
                EnableLiveMetrics = false,
                StorageDirectory = GetTelemetryStoragePath(),
            });

            builder.AddProcessor(new FilteringExportProcessor(
                new BatchActivityExportProcessor(new TagEnrichingExporter(azureMonitorExporter, tagsSource)),
                AspireCliTelemetry.ReportedActivitySourceName));

            _hasAzureMonitor = true;

#if DEBUG
            if (consoleExporterLevel == ConsoleExporterLevel.Reported)
            {
                builder.AddProcessor(new FilteringExportProcessor(
                    new SimpleActivityExportProcessor(new ConsoleActivityExporter(new ConsoleExporterOptions())),
                    AspireCliTelemetry.ReportedActivitySourceName));
            }
#endif
        }

        // Combined OTLP exporter: receives activities from both Profiling and Diagnostics sources.
        if (useOtlpExporter)
        {
            var otlpExporter = new OtlpTraceExporter(new OtlpExporterOptions());
            _profilingProcessor = new FilteringExportProcessor(
                new BatchActivityExportProcessor(new TagEnrichingExporter(otlpExporter, tagsSource)),
                ProfilingTelemetry.ActivitySourceName,
                AspireCliTelemetry.DiagnosticsActivitySourceName);

            builder.AddProcessor(_profilingProcessor);
            _hasProfilingProvider = useProfilingExporter;
            _hasDiagnosticProvider = true;
        }

        // Debug diagnostic console exporter: only receives activities from the Diagnostics source.
        if (useDiagnosticConsoleExporter)
        {
            builder.AddProcessor(new FilteringExportProcessor(
                new SimpleActivityExportProcessor(new ConsoleActivityExporter(new ConsoleExporterOptions())),
                AspireCliTelemetry.DiagnosticsActivitySourceName));

            _hasDiagnosticProvider = true;
        }

        _provider = builder.Build();
    }

    /// <summary>
    /// Gets whether Azure Monitor telemetry is enabled.
    /// </summary>
    public bool HasAzureMonitor => _hasAzureMonitor;

    /// <summary>
    /// Gets whether profiling telemetry export is enabled.
    /// </summary>
    public bool HasProfilingProvider => _hasProfilingProvider;

    /// <summary>
    /// Gets whether DEBUG-only diagnostic telemetry export is enabled.
    /// </summary>
    public bool HasDiagnosticProvider => _hasDiagnosticProvider;

    /// <summary>
    /// Flushes profiling telemetry without shutting down other telemetry providers.
    /// </summary>
    public Task ForceFlushProfilingAsync()
    {
        // Flush only the profiling processor rather than the entire provider so Azure Monitor
        // and diagnostic pipelines are not affected. The synchronous ForceFlush can block until
        // the batch exporter drains or the timeout expires, so run on the thread pool.
        return Task.Run(() =>
        {
            _profilingProcessor?.ForceFlush(ProfilingForceFlushTimeoutMilliseconds);
        });
    }

    /// <summary>
    /// Shuts down the telemetry providers, flushing any pending telemetry.
    /// </summary>
    public Task ShutdownAsync()
    {
        _shuttingDown = true;

        return Task.Run(() =>
        {
            _provider?.Shutdown(ShutDownTimeoutMilliseconds);
        });
    }

    private static string GetTelemetryStoragePath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".aspire", "cli", "telemetrystorage");
    }

    public void Dispose()
    {
        if (!_shuttingDown)
        {
            // Ensure everything is cleaned up for tests. This covers the situation where the host is disposed without a call to ShutdownAsync.
            // The shutdown timeout is zero so not to wait for telemetry to be flushed. Don't want to delay tests.
            // Dispose isn't used here because it always flushes telemetry and waits for completion.
            _provider?.Shutdown(0);
        }
    }
}
