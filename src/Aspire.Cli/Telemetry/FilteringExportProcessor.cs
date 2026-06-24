// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// A processor that filters activities by source name before forwarding them to an inner
/// export processor. This allows a single <see cref="OpenTelemetry.Trace.TracerProvider"/>
/// to route activities from different sources to different exporters.
/// </summary>
internal sealed class FilteringExportProcessor : BaseProcessor<Activity>
{
    private readonly BaseProcessor<Activity> _innerProcessor;
    private readonly string[] _allowedSourceNames;

    public FilteringExportProcessor(BaseProcessor<Activity> innerProcessor, params string[] allowedSourceNames)
    {
        _innerProcessor = innerProcessor;
        _allowedSourceNames = allowedSourceNames;
    }

    public override void OnEnd(Activity data)
    {
        if (_allowedSourceNames.Contains(data.Source.Name))
        {
            _innerProcessor.OnEnd(data);
        }
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
        => _innerProcessor.ForceFlush(timeoutMilliseconds);

    protected override bool OnShutdown(int timeoutMilliseconds)
        => _innerProcessor.Shutdown(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerProcessor.Dispose();
        }

        base.Dispose(disposing);
    }
}
