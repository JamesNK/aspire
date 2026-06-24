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
internal sealed class CliExportProcessor : BaseProcessor<Activity>
{
    private readonly TelemetryTagsSource _tagsSource;
    private readonly ILogger<CliExportProcessor> _logger;

    public CliExportProcessor(TelemetryTagsSource tagsSource, ILogger<CliExportProcessor> logger)
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

        // Add tags to activity events. ActivityEvent stores its tags as the original
        // IEnumerable<KeyValuePair<string, object?>> passed at construction. When an
        // ActivityTagsCollection was used (as in RecordError), we can cast back to it
        // and mutate in place.
        foreach (var activityEvent in activity.Events)
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
