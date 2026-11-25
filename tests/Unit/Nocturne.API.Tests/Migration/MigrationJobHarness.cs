using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.API.Services.Health;
using Nocturne.Infrastructure.Data.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Runs a real <see cref="MigrationJob"/> in API mode against a stubbed Nightscout, with the entry
/// import and every decomposer mocked out — what the source answers is the input under test, not
/// what the writers do with it.
/// </summary>
internal static class MigrationJobHarness
{
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }

        public bool IsResolved => Context is not null;

        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public void SetTenant(TenantContext? tenant) => Context = tenant;
    }

    /// <summary>Marks what it encrypts, so a test can tell stored ciphertext from plaintext, and reads only that back.</summary>
    internal sealed class MarkingSecrets : ISecretEncryptionService
    {
        private const string Mark = "encrypted:";

        public bool IsConfigured => true;

        public string Encrypt(string plaintext) => Mark + plaintext;

        public string Decrypt(string ciphertext) => CanDecrypt(ciphertext)
            ? ciphertext[Mark.Length..]
            : throw new ArgumentException("Invalid ciphertext format");

        public bool CanDecrypt(string? ciphertext) => ciphertext?.StartsWith(Mark, StringComparison.Ordinal) == true;

        public Dictionary<string, string> EncryptSecrets(Dictionary<string, string> secrets) =>
            secrets.ToDictionary(s => s.Key, s => Encrypt(s.Value));

        public Dictionary<string, string> DecryptSecrets(Dictionary<string, string> encryptedSecrets) =>
            encryptedSecrets.ToDictionary(s => s.Key, s => Decrypt(s.Value));
    }

    /// <summary>
    /// The service over <paramref name="provider"/>. A provider with no data services gets a factory
    /// that opens nothing, for the tests that never reach the database.
    /// </summary>
    public static MigrationJobService CreateService(
        IServiceProvider provider, IConfiguration? configuration = null, TimeProvider? time = null) => new(
        NullLogger<MigrationJobService>.Instance,
        provider,
        configuration ?? new ConfigurationBuilder().Build(),
        provider.GetService<INocturneDbContextFactory>() ?? Mock.Of<INocturneDbContextFactory>(),
        new MarkingSecrets(),
        time ?? TimeProvider.System);

    /// <summary>Runs the tenant's live job slice by slice until it ends.</summary>
    public static async Task RunToEndAsync(MigrationJobService service, TenantContext tenant)
    {
        using var timeout = new CancellationTokenSource(Eventually.DefaultPatience);
        while (await service.RunSliceAsync(tenant, timeout.Token))
        {
        }
    }

    /// <param name="entryOutcome">What the entry import reports for each page; empty when omitted.</param>
    /// <param name="treatmentOutcome">What the treatment import reports for each page; empty when omitted.</param>
    /// <param name="profileOutcome">What the profile decomposer reports for each profile; empty when omitted.</param>
    /// <param name="deviceStatusOutcome">What the device status import reports for each page; empty when omitted.</param>
    /// <param name="interceptor">Attached to every <see cref="NocturneDbContext"/> the provider creates.</param>
    /// <param name="database">Configures the store every context opens; a fresh in-memory one when omitted.</param>
    public static ServiceProvider BuildProvider(
        HttpMessageHandler handler,
        Func<IReadOnlyList<Entry>, DecompositionResult>? entryOutcome = null,
        Func<IReadOnlyList<Treatment>, DecompositionResult>? treatmentOutcome = null,
        Func<Profile, DecompositionResult>? profileOutcome = null,
        IInterceptor? interceptor = null,
        Func<IReadOnlyList<DeviceStatus>, DecompositionResult>? deviceStatusOutcome = null,
        Action<DbContextOptionsBuilder>? database = null)
    {
        var inMemory = $"migration-{Guid.NewGuid():N}";
        database ??= o => o.UseInMemoryDatabase(inMemory);

        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.ImportEntriesAsync(It.IsAny<IReadOnlyList<Entry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Entry> page, CancellationToken _) =>
                entryOutcome?.Invoke(page) ?? new DecompositionResult());

        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.ImportTreatmentsAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Treatment> page, CancellationToken _) =>
                treatmentOutcome?.Invoke(page) ?? new DecompositionResult());

        var deviceStatuses = new Mock<IDeviceStatusService>();
        deviceStatuses
            .Setup(s => s.ImportDeviceStatusesAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DeviceStatus> page, string? _, CancellationToken _) =>
                deviceStatusOutcome?.Invoke(page) ?? new DecompositionResult());

        var profiles = new Mock<IProfileDecomposer>();
        profiles
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Profile> batch, WriteOrigin _, CancellationToken _) =>
                profileOutcome?.Invoke(batch.Single()) ?? new DecompositionResult());

        var activities = new Mock<IActivityDecomposer>();
        activities
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Activity>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityDecomposition());

        return new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o =>
            {
                database(o);
                if (interceptor is not null)
                    o.AddInterceptors(interceptor);
            })
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
            .AddScoped<ITenantAccessor, FixedTenantAccessor>()
            .AddScoped<IAuditContext, AuditContext>()
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler))
            .AddSingleton(entries.Object)
            .AddSingleton(treatments.Object)
            .AddSingleton(deviceStatuses.Object)
            .AddSingleton(profiles.Object)
            .AddSingleton(activities.Object)
            .BuildServiceProvider();
    }

    public static Task<MigrationJobStatus> RunAsync(
        IServiceProvider provider, params string[] collections) =>
        RunAsync(provider, onCreated: null, collections);

    /// <summary>A tenant to run several jobs under, so their runs share one migration source row.</summary>
    public static TenantContext NewTenant(Guid? tenantId = null) => new(
        tenantId ?? Guid.CreateVersion7(), "migrated", "Migrated Tenant", true, IsDemo: false);

    /// <summary>
    /// <paramref name="onCreated"/> receives the job before it starts, so a test's stub source can
    /// cancel it mid-fetch the way the user's Cancel button does. The range is read as one part
    /// unless <paramref name="readFirst"/> is given: most stub sources here ignore the date filter,
    /// and what they test is how one part is paged.
    /// </summary>
    public static async Task<MigrationJobStatus> RunAsync(
        IServiceProvider provider,
        Action<MigrationJob>? onCreated,
        string[] collections,
        ILogger? logger = null,
        string nightscoutUrl = "https://example-nightscout.invalid",
        TenantContext? owner = null,
        Guid? tenantId = null,
        TimeProvider? time = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        TimeSpan? readFirst = null,
        string? apiSecret = null)
    {
        var tenant = owner ?? NewTenant(tenantId);

        var job = new MigrationJob(
            Guid.CreateVersion7(),
            tenant.TenantId,
            new StartMigrationRequest
            {
                Mode = MigrationMode.Api,
                NightscoutUrl = nightscoutUrl,
                NightscoutApiSecret = apiSecret,
                Collections = [.. collections],
                StartDate = startDate,
                EndDate = endDate,
            },
            new MigrationJobInfo
            {
                Id = Guid.CreateVersion7(),
                Mode = MigrationMode.Api,
                CreatedAt = DateTime.UtcNow,
            },
            tenant,
            logger ?? NullLogger.Instance,
            provider,
            time ?? TimeProvider.System)
        {
            ReadFirst = readFirst,
        };

        onCreated?.Invoke(job);

        await job.ExecuteAsync(TimeSpan.MaxValue, CancellationToken.None);

        return job.GetStatus();
    }
}
