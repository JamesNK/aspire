// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Dashboard.Tests.Shared;

internal static class TestDashboardDataSource
{
    public static DashboardDataSource Create(
        IDashboardRunStore runStore,
        DashboardDataSourcePool dataSourcePool)
    {
        return new DashboardDataSource(
            runStore,
            NullLogger<DashboardDataSource>.Instance,
            dataSourcePool);
    }

    public static DashboardDataSourcePool CreatePool(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository,
        IDashboardRunStore runStore)
    {
        return new DashboardDataSourcePool(
            runStore,
            new TestRepositoryFactory(telemetryRepository, resourceRepository));
    }

    private sealed class TestRepositoryFactory(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository) : IRepositoryFactory
    {
        public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) => telemetryRepository;
        public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) => resourceRepository;
    }
}

internal sealed class TestDashboardRunStore(
    IEnumerable<DashboardRunDescriptor>? runs = null,
    Func<DashboardRunDescriptor, IDisposable?>? tryAcquireRunLease = null,
    string? databasePath = null) : IDashboardRunStore
{
    private readonly IReadOnlyDictionary<string, DashboardRunDescriptor> _runs = (runs ??
        [new("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", databasePath ?? string.Empty, IsCurrent: true)])
        .ToDictionary(run => run.RunId, StringComparer.Ordinal);

    public bool SupportsRunSelection => _runs.Values.Any(run => !run.IsCurrent);

    public IReadOnlyDictionary<string, DashboardRunDescriptor> GetRuns() => _runs;

    public void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        _runs[run.RunId].IsPinned = isPinned;
    }

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => tryAcquireRunLease?.Invoke(run);
}