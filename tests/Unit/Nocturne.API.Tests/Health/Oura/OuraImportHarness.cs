using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Services.Health.Oura;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Health.Oura;

/// <summary>An <see cref="OuraImportJobService"/> over mocked writers, with the scopes a request and a job open.</summary>
internal sealed class OuraImportHarness
{
    public static readonly TenantContext Tenant = new(Guid.NewGuid(), "t", "T", true, false);

    public readonly TaskCompletionSource SleepGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly ConcurrentBag<Guid> WrittenUnder = [];
    public readonly TenantRunGuard RunGuard = new();
    public Func<IEnumerable<HeartRate>, IEnumerable<HeartRate>> StoreHeartRates = batch => batch;
    public Func<SleepSession, Task> StoreSession = _ => Task.CompletedTask;

    public sealed class Accessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    public (OuraImportJobService Jobs, ServiceProvider Root) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantAccessor, Accessor>();
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped(sp =>
        {
            var tenant = sp.GetRequiredService<ITenantAccessor>();
            var sleep = new Mock<ISleepService>();
            sleep.Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
                .Returns(async (SleepSession session, CancellationToken _) =>
                {
                    WrittenUnder.Add(tenant.TenantId);
                    await StoreSession(session);
                    return session;
                });
            var heartRates = new Mock<IHeartRateService>();
            heartRates.Setup(s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<HeartRate> batch, CancellationToken _) => StoreHeartRates(batch));
            var steps = new Mock<IStepCountService>();
            steps.Setup(s => s.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<StepCount> batch, CancellationToken _) => batch);
            return new OuraImportService(
                heartRates.Object, steps.Object, sleep.Object, Mock.Of<ISleepSessionRepository>(),
                NullLogger<OuraImportService>.Instance);
        });
        var root = services.BuildServiceProvider();
        return (new OuraImportJobService(root, RunGuard, TimeProvider.System, NullLogger<OuraImportJobService>.Instance), root);
    }

    public static TenantReader Caller(ServiceProvider root)
    {
        var request = root.CreateScope().ServiceProvider;
        request.GetRequiredService<ITenantAccessor>().SetTenant(Tenant);
        return TenantReader.Of(request);
    }

    public static byte[] Export() => OuraImportServiceTests.Export().ToArray();

    public static Task<OuraImportStatus> EndOf(OuraImportJobService jobs) =>
        Eventually.ValueAsync(
            "the import to end",
            () => Task.FromResult(jobs.GetLatest(Tenant.TenantId) is { State: not OuraImportState.Running } ended ? ended : null),
            step: TimeSpan.FromMilliseconds(20));
}
