// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// A processor that enriches activities with default telemetry tags (machine ID, OS, version, etc.)
/// before they are forwarded to export processors. Tags are calculated in the background by
/// <see cref="AspireCliTelemetry"/> and made available through <see cref="TelemetryTagsSource"/>.
/// </summary>
internal sealed class TagEnrichingProcessor : BaseProcessor<Activity>
{
    private readonly TelemetryTagsSource _tagsSource;

    public TagEnrichingProcessor(TelemetryTagsSource tagsSource)
    {
        _tagsSource = tagsSource;
    }

    public override void OnEnd(Activity data)
    {
        var task = _tagsSource.TagsTask;
        if (task.IsCompletedSuccessfully)
        {
            foreach (var tag in task.Result)
            {
                data.AddTag(tag.Key, tag.Value);
            }
        }
    }
}
