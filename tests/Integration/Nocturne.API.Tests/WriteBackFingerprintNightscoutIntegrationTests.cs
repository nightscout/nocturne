using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Treatments written back to a real Nightscout and read back as the connector reads them. Each copy
/// Nightscout serves must have the fingerprint write-back kept for the document it sent, whatever
/// Nightscout added or rewrote on storing it: otherwise every pull would apply the copy as an upstream
/// edit and send it back again.
/// </summary>
[Trait("Category", "Integration")]
public abstract class WriteBackFingerprintNightscoutIntegrationTests<TNightscout>(TNightscout fixture)
    : IClassFixture<TNightscout>
    where TNightscout : WriteBackFingerprintNightscout
{
    private readonly NightscoutContainer _nightscout = fixture.Container;
    private readonly List<WriteBackSentRecord> _kept = [];

    private NightscoutTreatmentWriteBackSink Sink()
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = _nightscout.BaseUrl,
            ApiSecret = NightscoutContainer.ApiSecret,
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        });
        var records = new Mock<IWriteBackSentRecords>();
        records.Setup(r => r.RecordSentAsync(It.IsAny<IReadOnlyList<WriteBackSentRecord>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<WriteBackSentRecord> sent, CancellationToken _) => _kept.AddRange(sent))
            .Returns(Task.CompletedTask);
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(), loader.Object, new NightscoutCircuitBreaker(), records.Object,
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private async Task<List<string>> ServedFingerprintsAsync()
    {
        var body = await _nightscout.Client.GetStringAsync("/api/v1/treatments.json?count=100&find[created_at][$gte]=2000-01-01");
        return [.. JsonNode.Parse(body)!.AsArray().Select(d =>
            WriteBackFingerprint.Of(JsonSerializer.Deserialize<Treatment>(d!.ToJsonString(), UpstreamIdentityJson.ReadOptions)!))];
    }

    private int _minutes;

    /// <remarks>
    /// Each at its own time: up to 15.0.6 a POST upserts by <c>created_at</c> and <c>eventType</c>.
    /// </remarks>
    private Treatment Stored(string legacyId, Action<Treatment> fill)
    {
        var treatment = new Treatment
        {
            Id = Guid.NewGuid().ToString(),
            RecordId = Guid.NewGuid(),
            LegacyId = legacyId,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(_minutes += 5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            EnteredBy = "nocturne",
            DataSource = "aaps",
        };
        fill(treatment);
        return treatment;
    }

    [Fact]
    public async Task EveryCopyServed_HasTheFingerprintWriteBackKept()
    {
        var sink = Sink();
        Treatment[] created =
        [
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Correction Bolus"; t.Insulin = 1.25; }),
            Stored(MongoObjectId.NewObjectId(), t =>
            {
                t.EventType = "Correction Bolus"; t.Insulin = 0.4; t.BolusType = "SMB"; t.Automatic = true;
                t.InsulinDelivered = 0.35; t.InsulinProgrammed = 0.4;
            }),
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Correction Bolus"; t.Insulin = 0.6; t.BolusType = "NORMAL"; t.Automatic = false; }),
            Stored($"aaps-{Guid.NewGuid():N}", t => { t.EventType = "Meal Bolus"; t.Insulin = 3; t.Carbs = 45; t.Notes = "lunch"; }),
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Temp Basal"; t.Absolute = 0.85; t.Rate = 0.85; t.Duration = 30; }),
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Temporary Target"; t.TargetTop = 140; t.TargetBottom = 120; t.Duration = 60; t.Reason = "Activity"; }),
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "BG Check"; t.Glucose = 112; t.GlucoseType = "Finger"; t.Units = "mg/dl"; }),
            Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Meal Bolus"; t.Insulin = 2; t.Carbs = 30; t.PreBolus = 15; }),
        ];
        await sink.OnCreatedAsync(created);
        var edited = Stored(MongoObjectId.NewObjectId(), t => { t.EventType = "Correction Bolus"; t.Insulin = 0.5; });
        await sink.OnCreatedAsync([edited]);
        edited.Insulin = 0.75;
        await sink.OnUpdatedAsync(edited);

        var served = await ServedFingerprintsAsync();

        served.Should().HaveCount(created.Length + 2);
        var kept = _kept.GroupBy(k => k.LegacyId).SelectMany(g => g.Last().Documents).ToList();
        served.Should().BeEquivalentTo(kept);
    }
}

/// <summary>A Nightscout of one image, started for the class that writes back to it.</summary>
public abstract class WriteBackFingerprintNightscout(string image) : IAsyncLifetime
{
    public NightscoutContainer Container { get; } = new(image);

    public Task InitializeAsync() => Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}

public sealed class PinnedWriteBackFingerprintNightscout() : WriteBackFingerprintNightscout(NightscoutContainer.PinnedImage);

public sealed class LatestDevWriteBackFingerprintNightscout()
    : WriteBackFingerprintNightscout("nightscout/cgm-remote-monitor:latest_dev");

public sealed class WriteBackFingerprintPinnedNightscoutIntegrationTests(PinnedWriteBackFingerprintNightscout fixture)
    : WriteBackFingerprintNightscoutIntegrationTests<PinnedWriteBackFingerprintNightscout>(fixture);

public sealed class WriteBackFingerprintLatestDevNightscoutIntegrationTests(LatestDevWriteBackFingerprintNightscout fixture)
    : WriteBackFingerprintNightscoutIntegrationTests<LatestDevWriteBackFingerprintNightscout>(fixture);
