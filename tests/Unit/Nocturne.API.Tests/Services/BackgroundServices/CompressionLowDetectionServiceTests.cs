using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// Detection reads the tenant's enable flag, bedtime and wake time from
/// <see cref="IUISettingsService"/>, so a read it cannot trust leaves it with no window to analyse.
/// </summary>
[Trait("Category", "Unit")]
public class CompressionLowDetectionServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly NightOf = new(2026, 4, 17);

    private readonly Mock<ICompressionLowRepository> _repository = new();
    private readonly Mock<IEntryService> _entryService = new();

    [Fact]
    public async Task DetectForNightAsync_doesNotDetectOnDefaultSettingsWhenTheSettingsReadFails()
    {
        var sut = new CompressionLowDetectionService(
            await BrokenSettingsProviderAsync(),
            ActiveTenantSnapshotTestDoubles.Unread(),
            NullLogger<CompressionLowDetectionService>.Instance
        );

        var created = await sut.DetectForNightAsync(NightOf);

        created.Should().Be(0);
        _repository.Verify(
            r =>
                r.ActiveSuggestionsExistForNightAsync(
                    It.IsAny<DateOnly>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _entryService.Verify(
            e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    /// <summary>
    /// A provider whose <see cref="IUISettingsService"/> is the real one over a database that can no
    /// longer be read, which is what a failed read looks like from inside the scope.
    /// </summary>
    private async Task<IServiceProvider> BrokenSettingsProviderAsync()
    {
        _entryService
            .Setup(e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Entry>());

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = TenantId };
        var settingsService = new UISettingsService(
            context,
            NullLogger<UISettingsService>.Instance
        );
        await context.DisposeAsync();

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var services = new ServiceCollection();
        services.AddScoped<IUISettingsService>(_ => settingsService);
        services.AddScoped(_ => _repository.Object);
        services.AddScoped(_ => _entryService.Object);
        services.AddScoped(_ => Mock.Of<ITreatmentService>());
        services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
        services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
        services.AddScoped(_ => tenantAccessor.Object);

        return services.BuildServiceProvider();
    }
}
