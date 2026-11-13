using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;
using OrefModels = Nocturne.Core.Oref.Models;

namespace Nocturne.API.Tests.Services.Glucose;

public class PredictionServiceTests
{
    private readonly Mock<IEntryService> _entries = new();

    [Fact]
    public async Task OrefUnavailable_Throws_AndReadsNoGlucose()
    {
        SeedCurrentEntry();
        var service = Build(orefVersion: null, status: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPredictionsAsync());

        Assert.Equal("The oref library is not available", ex.Message);
        _entries.Verify(e => e.GetCurrentEntryAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GlucoseStatusNull_Throws_AndReturnsNoCurve()
    {
        SeedCurrentEntry();
        _entries
            .Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Entry { Sgv = 150, Mgdl = 150, Mills = 1_000_000 } });
        var service = Build(orefVersion: "test", status: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPredictionsAsync());

        Assert.Equal("The glucose status could not be calculated", ex.Message);
        _entries.Verify(e => e.GetCurrentEntryAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A current reading is all a synthetic curve would need, so a service that still built one would return it instead of throwing.</summary>
    private void SeedCurrentEntry() =>
        _entries
            .Setup(e => e.GetCurrentEntryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Entry { Sgv = 150, Mgdl = 150, Delta = 3, Mills = 1_000_000 });

    private TestablePredictionService Build(string? orefVersion, OrefModels.GlucoseStatus? status) =>
        new(
            _entries.Object,
            Mock.Of<ITreatmentService>(),
            Mock.Of<IBasalRateResolver>(),
            Mock.Of<ISensitivityResolver>(),
            Mock.Of<ICarbRatioResolver>(),
            Mock.Of<ITargetRangeResolver>(),
            Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-22T12:00:00Z")),
            NullLogger<PredictionService>.Instance)
        {
            Version = orefVersion,
            Status = status,
        };

    private sealed class TestablePredictionService(
        IEntryService entries,
        ITreatmentService treatments,
        IBasalRateResolver basalRate,
        ISensitivityResolver sensitivity,
        ICarbRatioResolver carbRatio,
        ITargetRangeResolver targetRange,
        ITherapySettingsResolver therapySettings,
        IPatientInsulinRepository insulins,
        TimeProvider timeProvider,
        Microsoft.Extensions.Logging.ILogger<PredictionService> logger)
        : PredictionService(entries, treatments, basalRate, sensitivity, carbRatio, targetRange,
            therapySettings, insulins, timeProvider, logger)
    {
        public string? Version { get; init; }
        public OrefModels.GlucoseStatus? Status { get; init; }

        internal override string? OrefVersion() => Version;

        internal override OrefModels.GlucoseStatus? CalculateGlucoseStatus(
            IEnumerable<OrefModels.GlucoseReading> readings) => Status;
    }
}
