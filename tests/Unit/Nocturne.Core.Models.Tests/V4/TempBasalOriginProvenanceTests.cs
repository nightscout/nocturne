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
    [Theory]
    [InlineData(TempBasalOrigin.Manual, null, "Trio", null, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Algorithm, null, "loop://iPhone", null, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, null, "openaps://Lotus", DataSources.NightscoutConnector, TempBasalOriginEvidence.Uploader)]
    [InlineData(TempBasalOrigin.Manual, false, "Trio", null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Manual, null, "xdrip", null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Manual, null, null, null, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, null, DataSources.CareLinkConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Algorithm, null, "loop://iPhone", DataSources.TConnectSyncConnector, TempBasalOriginEvidence.Reported)]
    [InlineData(TempBasalOrigin.Suspended, null, "Trio", null, TempBasalOriginEvidence.Reported)]
    public void A_row_stored_without_evidence_is_a_default_only_from_the_decomposer_for_a_loop(
        TempBasalOrigin origin, bool? flag, string? enteredBy, string? dataSource, TempBasalOriginEvidence expected)
    {
        TempBasalOriginProvenance.EvidenceOf(origin, stored: null, flag, enteredBy, dataSource).Should().Be(expected);
    }

    [Fact]
    public void Stored_evidence_stands()
    {
        TempBasalOriginProvenance.EvidenceOf(
                TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, automaticFlag: null, "xdrip", DataSources.CareLinkConnector)
            .Should().Be(TempBasalOriginEvidence.Snapshot);
    }
}
