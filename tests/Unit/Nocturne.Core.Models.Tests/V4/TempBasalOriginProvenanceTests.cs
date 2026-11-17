using FluentAssertions;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// A row stored before evidence was recorded is a default only when the decomposer wrote it for a
/// closed-loop uploader without a flag; anything else keeps its origin as stated.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalOriginProvenanceTests
{
    private static readonly Guid Snapshot = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTime Start = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TempBasalOriginState State(
        TempBasalOrigin origin,
        TempBasalOriginEvidence? evidence,
        Guid? snapshot,
        DateTime? start = null,
        double? rate = 1.2) =>
        new(origin, evidence, snapshot, new DateTimeOffset(start ?? Start).ToUnixTimeMilliseconds(), rate);

    [Theory]
    [InlineData(TempBasalOriginEvidence.Reported, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOriginEvidence.Reported, null)]
    [InlineData(TempBasalOriginEvidence.Snapshot, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOriginEvidence.Snapshot, null)]
    public void OnUpdate_IncomingDefaultKeepsAStoredStatementOrJudgement(
        TempBasalOriginEvidence stored, TempBasalOriginEvidence? incoming)
    {
        var kept = State(TempBasalOrigin.Manual, stored, Snapshot);

        var result = TempBasalOriginProvenance.OnUpdate(kept, State(TempBasalOrigin.Algorithm, incoming, null));

        result.Should().Be(kept);
    }

    [Fact]
    public void OnUpdate_AJudgementStaysThroughFloatingPointNoiseInTheRateAndSubMillisecondStart()
    {
        var kept = State(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, Snapshot);

        var result = TempBasalOriginProvenance.OnUpdate(
            kept,
            State(
                TempBasalOrigin.Algorithm,
                TempBasalOriginEvidence.Uploader,
                null,
                start: Start.AddTicks(4567),
                rate: 1.2 + 1e-9));

        result.Should().Be(kept);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.21)]
    [InlineData(1.25)]
    [InlineData(null)]
    public void OnUpdate_AJudgementIsTakenAgainWhenTheRateChanges(double? incomingRate)
    {
        var incoming = State(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.Uploader, null, rate: incomingRate);

        var result = TempBasalOriginProvenance.OnUpdate(
            State(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, Snapshot), incoming);

        result.Should().Be(incoming);
    }

    [Fact]
    public void OnUpdate_AJudgementIsTakenAgainWhenTheStartChanges()
    {
        var incoming = State(
            TempBasalOrigin.Algorithm, TempBasalOriginEvidence.Uploader, null, start: Start.AddMinutes(1));

        var result = TempBasalOriginProvenance.OnUpdate(
            State(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, Snapshot), incoming);

        result.Should().Be(incoming);
    }

    [Fact]
    public void OnUpdate_AStatementStaysWhenTheStartAndRateChange()
    {
        var kept = State(TempBasalOrigin.Manual, TempBasalOriginEvidence.Reported, null);

        var result = TempBasalOriginProvenance.OnUpdate(
            kept,
            State(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.Uploader, null, start: Start.AddMinutes(1), rate: 0.4));

        result.Should().Be(kept);
    }

    [Theory]
    [InlineData(TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOriginEvidence.Snapshot)]
    public void OnUpdate_IncomingStatementOverwritesAllThreeFields(TempBasalOriginEvidence stored)
    {
        var incoming = State(TempBasalOrigin.Manual, TempBasalOriginEvidence.Reported, null);

        var result = TempBasalOriginProvenance.OnUpdate(
            State(TempBasalOrigin.Algorithm, stored, Snapshot), incoming);

        result.Should().Be(incoming);
    }

    [Theory]
    [InlineData(TempBasalOriginEvidence.Uploader, TempBasalOriginEvidence.Snapshot)]
    [InlineData(TempBasalOriginEvidence.Uploader, TempBasalOriginEvidence.Uncorroborated)]
    [InlineData(TempBasalOriginEvidence.Uncorroborated, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOriginEvidence.Uploader, TempBasalOriginEvidence.Uploader)]
    [InlineData(null, TempBasalOriginEvidence.Uploader)]
    public void OnUpdate_OtherwiseTakesTheIncomingState(
        TempBasalOriginEvidence? stored, TempBasalOriginEvidence incomingEvidence)
    {
        var incoming = State(TempBasalOrigin.Manual, incomingEvidence, Snapshot);

        var result = TempBasalOriginProvenance.OnUpdate(
            State(TempBasalOrigin.Algorithm, stored, null), incoming);

        result.Should().Be(incoming);
    }

    [Theory]
    [InlineData(TempBasalOrigin.Manual, null, "Trio", null, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Algorithm, null, "loop://iPhone", null, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, null, "openaps://Lotus", DataSources.NightscoutConnector, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, false, "Trio", null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Manual, null, "xdrip", null, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, null, null, DataSources.NightscoutConnector, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, null, "tidepool", DataSources.NightscoutConnector, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, true, null, null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, null, null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, "tidepool", null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, null, DataSources.CareLinkConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Manual, null, null, DataSources.CareLinkConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Manual, null, null, DataSources.GlookoConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, "loop://iPhone", DataSources.TConnectSyncConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Suspended, null, "Trio", null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Suspended, null, null, null, TempBasalOriginEvidence.Reported)]
    public void A_row_stored_without_evidence_is_a_default_when_the_decomposer_gave_it_its_default(
        TempBasalOrigin origin, bool? flag, string? enteredBy, string? dataSource, TempBasalOriginEvidence expected)
    {
        TempBasalOriginProvenance.EvidenceOf(origin, stored: null, flag, enteredBy, dataSource, LegacyId).Should().Be(expected);
    }

    private const string LegacyId = "6527c1f2a8b4e90012345678";

    [Theory]
    [InlineData(null, TempBasalOriginEvidence.Reported)]
    [InlineData("xdrip", TempBasalOriginEvidence.Reported)]
    [InlineData("Trio", TempBasalOriginEvidence.Uploader)]
    public void A_manual_temp_with_no_legacy_id_is_a_default_only_from_a_closed_loop_uploader(
        string? enteredBy, TempBasalOriginEvidence expected)
    {
        TempBasalOriginProvenance.EvidenceOf(
                TempBasalOrigin.Manual, stored: null, automaticFlag: null, enteredBy, dataSource: null, legacyId: null)
            .Should().Be(expected);
    }

    [Fact]
    public void Stored_evidence_stands()
    {
        TempBasalOriginProvenance.EvidenceOf(
                TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, automaticFlag: null, "xdrip", DataSources.CareLinkConnector, LegacyId)
            .Should().Be(TempBasalOriginEvidence.Snapshot);
    }
}
