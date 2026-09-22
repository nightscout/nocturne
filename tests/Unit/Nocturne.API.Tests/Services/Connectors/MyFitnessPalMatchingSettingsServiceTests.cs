using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The matching settings carry the notification switch and the window a food entry is matched
/// within, so the same invariant as <see cref="Nocturne.Core.Contracts.Profiles.IUISettingsService"/>
/// applies: a tenant that has saved nothing is not a read that failed.
/// </summary>
[Trait("Category", "Unit")]
public class MyFitnessPalMatchingSettingsServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task GetSettingsAsync_returnsNullWhenTheReadFails()
    {
        var context = NewContext();
        var service = NewService(context);
        await context.DisposeAsync();

        (await service.GetSettingsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetSettingsAsync_returnsDefaultsWhenTheTenantHasSavedNothing()
    {
        var stored = await NewService(NewContext()).GetSettingsAsync();

        stored.Should().BeEquivalentTo(new MyFitnessPalMatchingSettings());
    }

    [Fact]
    public async Task GetSettingsAsync_readsBackWhatWasSaved()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveSettingsAsync(
            new MyFitnessPalMatchingSettings
            {
                EnableMatchNotifications = false,
                MatchTimeWindowMinutes = 17,
            }
        );

        var stored = await service.GetSettingsAsync();

        stored.Should().NotBeNull();
        stored!.EnableMatchNotifications.Should().BeFalse();
        stored.MatchTimeWindowMinutes.Should().Be(17);
    }

    private static NocturneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NocturneDbContext(options) { TenantId = TenantId };
    }

    private static IMyFitnessPalMatchingSettingsService NewService(NocturneDbContext context)
    {
        return new MyFitnessPalMatchingSettingsService(
            context,
            NullLogger<MyFitnessPalMatchingSettingsService>.Instance
        );
    }
}
