// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Holds the background task that calculates default telemetry tags (machine ID, OS info, etc.).
/// Shared between <see cref="AspireCliTelemetry"/> (which starts the calculation) and
/// <see cref="CliTagEnrichmentProcessor"/> (which applies the tags to activities before export).
/// </summary>
internal sealed class TelemetryTagsSource
{
    private volatile Task<IReadOnlyList<KeyValuePair<string, object?>>>? _tagsTask;

    /// <summary>
    /// Gets the task that resolves to the calculated tags. Returns an empty list if
    /// calculation has not been started yet.
    /// </summary>
    public Task<IReadOnlyList<KeyValuePair<string, object?>>> TagsTask =>
        _tagsTask ?? Task.FromResult<IReadOnlyList<KeyValuePair<string, object?>>>(Array.Empty<KeyValuePair<string, object?>>());

    /// <summary>
    /// Starts the background tag calculation. Only the first call takes effect; subsequent
    /// calls are ignored.
    /// </summary>
    public void StartCalculation(Func<Task<IReadOnlyList<KeyValuePair<string, object?>>>> factory)
    {
        _tagsTask ??= Task.Run(factory);
    }
}
