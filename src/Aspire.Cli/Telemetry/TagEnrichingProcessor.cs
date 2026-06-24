// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// An exporter that enriches activities with default telemetry tags (machine ID, OS, version, etc.)
/// before forwarding them to an inner exporter. Tags are calculated in the background by
/// <see cref="AspireCliTelemetry"/> and made available through <see cref="TelemetryTagsSource"/>.
/// This exporter is intended to be used inside a <see cref="BatchActivityExportProcessor"/> so
/// blocking on tag calculation runs on the batch export thread rather than the calling thread.
/// </summary>
internal sealed class TagEnrichingExporter : BaseExporter<Activity>
{
    private readonly BaseExporter<Activity> _innerExporter;
    private readonly TelemetryTagsSource _tagsSource;

    // Guards against concurrent enrichment of the same activity from multiple
    // TagEnrichingExporter instances running on different batch export threads.
    private static readonly Lock s_enrichLock = new();

    public TagEnrichingExporter(BaseExporter<Activity> innerExporter, TelemetryTagsSource tagsSource)
    {
        _innerExporter = innerExporter;
        _tagsSource = tagsSource;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        // Block until tags are available. This is safe because this exporter runs inside a
        // BatchActivityExportProcessor whose Export call is on a background thread.
        var tags = _tagsSource.TagsTask.GetAwaiter().GetResult();

        // Enumerate the batch (destructive for CircularBuffer-backed batches), enrich each
        // activity, and collect them so we can forward to the inner exporter in one call.
        var activities = new List<Activity>();

        // Multiple TagEnrichingExporter instances may see the same activity (e.g. Azure
        // Monitor and OTLP). Only enrich once to avoid duplicate tags by checking whether
        // the first tag has already been set.
        lock (s_enrichLock)
        {
            foreach (var activity in batch)
            {
                if (tags.Count > 0 && activity.GetTagItem(tags[0].Key) is null)
                {
                    foreach (var tag in tags)
                    {
                        activity.SetTag(tag.Key, tag.Value);
                    }
                }

                activities.Add(activity);
            }
        }

        var enrichedBatch = new Batch<Activity>([.. activities], activities.Count);
        return _innerExporter.Export(enrichedBatch);
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
        => _innerExporter.Shutdown(timeoutMilliseconds);

    protected override bool OnForceFlush(int timeoutMilliseconds)
        => _innerExporter.ForceFlush(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerExporter.Dispose();
        }

        base.Dispose(disposing);
    }
}
