using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// The service's own logic: which reads feed the episodes, and how the finger sticks are gathered.
/// The episodes themselves are covered by <c>ErroneousReadingEpisodesTests</c>.
/// </summary>
public class ErroneousReadingsServiceTests
{
    private static readonly DateTime From = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);
    private static readonly DateTime Onset = From.AddHours(14);

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<IStateSpanService> _spans = new();
    private readonly Mock<INoteRepository> _notes = new();
    private readonly Mock<IMeterGlucoseRepository> _meters = new();
    private readonly Mock<IBGCheckRepository> _checks = new();
    private readonly Mock<ITherapySettingsResolver> _therapy = new();

    public ErroneousReadingsServiceTests()
    {
        _spans.Setup(s => s.GetStateSpansAsync(
                It.IsAny<StateSpanCategory?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _notes.Setup(n => n.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private ErroneousReadingsService Service() =>
        new(_glucose.Object, _spans.Object, _notes.Object, _meters.Object, _checks.Object,
            new TenantClockResolver(_therapy.Object, new CategoryReadContext(), new ShareClockDouble(_therapy.Object),
                NullLogger<TenantClockResolver>.Instance));

    [Fact]
    public async Task AnEpisode_CarriesEachFingerStickOnce_AndNotASensorCheck()
    {
        _glucose.Setup(g => g.GetCanonicalWithUncountedAsync(
                From, To, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Invalid(0, 50), Invalid(5, 48), Invalid(10, 52)]);
        _meters.Setup(m => m.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MeterGlucose { Timestamp = Onset.AddMinutes(5), Mgdl = 110 }]);
        _checks.Setup(c => c.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BGCheck { Timestamp = Onset.AddMinutes(5), Glucose = 110, GlucoseType = GlucoseType.Finger },
                new BGCheck { Timestamp = Onset.AddMinutes(10), Glucose = 52, GlucoseType = GlucoseType.Sensor },
            ]);

        var report = await Service().GetAsync(From, To);

        report.Days.Should().ContainSingle().Which.Episodes.Should().ContainSingle()
            .Which.MeterReadings.Should().ContainSingle().Which.Mgdl.Should().Be(110);
    }

    [Fact]
    public async Task TheDays_AreCutInThePatientsZone()
    {
        _therapy.Setup(t => t.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Pacific/Auckland");
        _glucose.Setup(g => g.GetCanonicalWithUncountedAsync(
                From, To, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Invalid(0, 50), Invalid(5, 48)]);
        _meters.Setup(m => m.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _checks.Setup(c => c.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var report = await Service().GetAsync(From, To);

        // 14:00 UTC on 2 March is 03:00 on 3 March in Auckland (UTC+13).
        report.Days.Should().ContainSingle().Which.Date.Should().Be("2026-03-03");
    }

    private static SensorGlucose Invalid(int minute, double mgdl) => new()
    {
        Timestamp = Onset.AddMinutes(minute),
        Mgdl = mgdl,
        Quality = SensorGlucoseQuality.Invalid,
    };
}
