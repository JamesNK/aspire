// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Processor that applies background-calculated telemetry tags to activities and their events
/// before export. Tags are sourced from <see cref="TelemetryTagsSource"/> which computes
/// machine/identity information asynchronously at startup.
/// </summary>
internal sealed class CliTagEnrichmentProcessor : BaseProcessor<Activity>
{
    private readonly TelemetryTagsSource _tagsSource;
    private readonly ILogger<CliTagEnrichmentProcessor> _logger;

    public CliTagEnrichmentProcessor(TelemetryTagsSource tagsSource, ILogger<CliTagEnrichmentProcessor> logger)
    {
        _tagsSource = tagsSource;
        _logger = logger;
    }

    public override void OnEnd(Activity activity)
    {
        var tagsTask = _tagsSource.TagsTask;

        IReadOnlyList<KeyValuePair<string, object?>> tags;

        if (tagsTask.IsCompletedSuccessfully)
        {
            tags = tagsTask.Result;
        }
        else
        {
            var stopwatch = Stopwatch.StartNew();
            tags = tagsTask.GetAwaiter().GetResult();
            stopwatch.Stop();

            _logger.LogDebug("CliExportProcessor: blocked {ElapsedMilliseconds}ms waiting for telemetry tags to be calculated.", stopwatch.ElapsedMilliseconds);
        }

        // Add tags to the activity itself.
        foreach (var tag in tags)
        {
            activity.SetTag(tag.Key, tag.Value);
        }

        // Add tags to activity events. The runtime wraps event tags in an internal
        // TagsLinkedList, so the cast to ActivityTagsCollection only succeeds when
        // the event was constructed without going through Activity.AddEvent (rare).
        // When it does succeed we enrich in place; otherwise the activity-level tags
        // already carry the same information for the exporter.
        foreach (ref readonly var activityEvent in activity.EnumerateEvents())
        {
            if (activityEvent.Tags is ActivityTagsCollection mutableTags)
            {
                foreach (var tag in tags)
                {
                    mutableTags[tag.Key] = tag.Value;
                }
            }
        }
    }
}
