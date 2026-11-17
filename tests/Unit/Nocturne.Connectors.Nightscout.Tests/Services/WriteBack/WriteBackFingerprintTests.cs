using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// <see cref="WriteBackFingerprint"/> of a document as write-back sends it and of the copy Nightscout
/// serves back, both read as the connector's pull reads them. An unchanged copy must match what was
/// sent, or every pull would apply it as an edit and send it back again.
/// </summary>
[Trait("Category", "Unit")]
public class WriteBackFingerprintTests
{
    private static string Pulled(string json) =>
        WriteBackFingerprint.Of(JsonSerializer.Deserialize<Treatment>(json, UpstreamIdentityJson.ReadOptions)!);

    private static Treatment Bolus(double insulin = 1.5) => new()
    {
        Id = "66f0a1b2c3d4e0f6a7b8c9d0",
        LegacyId = "66f0a1b2c3d4e0f6a7b8c9d0",
        EventType = "Meal Bolus",
        Insulin = insulin,
        Carbs = 30,
        Notes = "lunch",
        EnteredBy = "nocturne",
        CreatedAt = "2026-09-01T08:00:00Z",
        DataSource = "aaps",
    };

    [Fact]
    public void TheCopyNightscoutStores_MatchesWhatWasSent()
    {
        var sent = NightscoutTreatmentWriteBackSink.AsPulled(Bolus());

        Pulled("""
            {
              "srvModified": 1788249600123, "utcOffset": 0, "notes": "lunch", "carbs": 30.0,
              "_id": "66f0a1b2c3d4e0f6a7b8c9d1", "identifier": "66f0a1b2c3d4e0f6a7b8c9d0",
              "created_at": "2026-09-01T08:00:00.000Z", "insulin": 1.50, "eventType": "Meal Bolus",
              "enteredBy": "nocturne", "srvCreated": 1788249600123, "glucose": null, "protein": 0, "duration": 0
            }
            """).Should().Be(sent);
    }

    [Fact]
    public void TheTimestampCountsByInstantNotByFormat() =>
        Pulled("""{"eventType":"Note","notes":"x","created_at":"2026-09-01T10:00:00+02:00"}""")
            .Should().Be(Pulled("""{"eventType":"Note","notes":"x","created_at":"2026-09-01T08:00:00.000Z"}"""));

    [Theory]
    [InlineData("""{"eventType":"Meal Bolus","insulin":2.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z"}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":45,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z"}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:05:00.000Z"}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"dinner","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z"}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","isValid":false}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","type":"SMB"}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","automatic":true}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","insulinDelivered":1.2}""")]
    [InlineData("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","insulinProgrammed":1.6}""")]
    public void AnEditMadeUpstream_DiffersFromWhatWasSent(string edited) =>
        Pulled(edited).Should().NotBe(NightscoutTreatmentWriteBackSink.AsPulled(Bolus()));

    [Fact]
    public void AValidFlag_CountsOnlyWhenFalse() =>
        Pulled("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","isValid":true}""")
            .Should().Be(NightscoutTreatmentWriteBackSink.AsPulled(Bolus()));

    [Fact]
    public void AnAutomaticFlag_CountsOnlyWhenTrue() =>
        Pulled("""{"eventType":"Meal Bolus","insulin":1.5,"carbs":30,"notes":"lunch","enteredBy":"nocturne","created_at":"2026-09-01T08:00:00.000Z","automatic":false}""")
            .Should().Be(NightscoutTreatmentWriteBackSink.AsPulled(Bolus()));

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    private static IEnumerable<string[]> SendsOf(string? stored) => WriteBackFingerprint.Sends(stored!);

    [Fact]
    public void WhatARowStores_NamesEachDocumentItsRecordWentAs()
    {
        var stored = WriteBackFingerprint.Stored(["bolus-half", "carbs-half"]);

        WriteBackFingerprint.MayBeSent(stored, "bolus-half", Now).Should().BeTrue();
        WriteBackFingerprint.MayBeSent(stored, "carbs-half", Now).Should().BeTrue();
        WriteBackFingerprint.MayBeSent(stored, "edited", Now).Should().BeFalse();
        SendsOf(stored).Should().BeEquivalentTo(new[] { new[] { "bolus-half", "carbs-half" } });
    }

    [Fact]
    public void ASendBegun_IsAddedOnlyToARowThatKeepsAnAcceptedOne()
    {
        WriteBackFingerprint.Sending(null, ["b", "c"], Now).Should().BeNull();

        var sending = WriteBackFingerprint.Sending("w", ["b", "c"], Now);

        WriteBackFingerprint.MayBeSent(sending!, "c", Now).Should().BeTrue();
        SendsOf(sending).Should().BeEquivalentTo(new[] { new[] { "w" }, ["b", "c"] }, o => o.WithStrictOrdering());
        WriteBackFingerprint.Sending(sending, ["b", "c"], Now).Should().Be(sending);
        WriteBackFingerprint.Sending("w", ["w"], Now).Should().Be("w");
    }

    [Fact]
    public void ASendBegunAgain_BecomesTheLatest_SoAcceptingItLetsGoOfThoseBegunBefore()
    {
        var split = WriteBackFingerprint.Sending("w", ["b", "c"], Now);

        var resent = WriteBackFingerprint.Sending(split, ["w"], Now);

        SendsOf(resent).Should().BeEquivalentTo(new[] { new[] { "w" }, ["b", "c"], ["w"] }, o => o.WithStrictOrdering());
        var accepted = WriteBackFingerprint.Accepted(resent, ["w"], Now);
        SendsOf(accepted).Should().BeEquivalentTo(new[] { new[] { "w" } });
        WriteBackFingerprint.MayBeSent(accepted, "b", Now).Should().BeTrue();

        var reordered = WriteBackFingerprint.Sending(WriteBackFingerprint.Sending(split, ["x"], Now), ["b", "c"], Now);
        SendsOf(WriteBackFingerprint.Accepted(reordered, ["b", "c"], Now))
            .Should().BeEquivalentTo(new[] { new[] { "b", "c" } });
    }

    [Fact]
    public void AnAcceptedSend_KeepsThoseBegunAfter_AndKeepsTheRestAsEarlierSends()
    {
        var stored = WriteBackFingerprint.Sending(WriteBackFingerprint.Sending(
            WriteBackFingerprint.Sending("w", ["x"], Now), ["b", "c"], Now), ["y"], Now);

        var accepted = WriteBackFingerprint.Accepted(stored, ["b", "c"], Now);
        SendsOf(accepted).Should().BeEquivalentTo(new[] { new[] { "b", "c" }, ["y"] }, o => o.WithStrictOrdering());
        WriteBackFingerprint.MayBeSent(accepted, "w", Now).Should().BeTrue();
        WriteBackFingerprint.MayBeSent(accepted, "x", Now).Should().BeTrue();

        SendsOf(WriteBackFingerprint.Accepted(stored, ["edited"], Now))
            .Should().BeEquivalentTo(new[] { new[] { "edited" }, ["x"], ["b", "c"], ["y"] }, o => o.WithStrictOrdering());
        WriteBackFingerprint.Accepted(null, ["b", "c"], Now).Should().Be(WriteBackFingerprint.Stored(["b", "c"]));
        SendsOf(WriteBackFingerprint.Accepted(stored, ["y"], Now)).Should().BeEquivalentTo(new[] { new[] { "y" } });
    }

    [Fact]
    public void TheSendAnAcceptedOneReplaced_StaysAnEcho_UntilItsLifeEnds()
    {
        var accepted = WriteBackFingerprint.Accepted(WriteBackFingerprint.Sending("w", ["w2"], Now), ["w2"], Now);

        SendsOf(accepted).Should().BeEquivalentTo(new[] { new[] { "w2" } });
        WriteBackFingerprint.MayBeSent(accepted, "w", Now + WriteBackFingerprint.EarlierSendLife - TimeSpan.FromSeconds(1))
            .Should().BeTrue();
        WriteBackFingerprint.MayBeSent(accepted, "w", Now + WriteBackFingerprint.EarlierSendLife).Should().BeFalse();

        var later = Now + WriteBackFingerprint.EarlierSendLife;
        WriteBackFingerprint.Accepted(accepted, ["w3"], later)
            .Should().Be(WriteBackFingerprint.Accepted(WriteBackFingerprint.Stored(["w2"]), ["w3"], later));
    }

    [Fact]
    public void AnEarlierSendLetGoBeforeItsLifeEnds_MakesEveryCopyAnEcho_UntilIt()
    {
        var stored = "s0";
        for (var i = 1; i <= WriteBackFingerprint.MaxEarlierSends + 1; i++)
            stored = WriteBackFingerprint.Accepted(stored, [$"s{i}"], Now + TimeSpan.FromSeconds(i));

        var lostAt = Now + TimeSpan.FromSeconds(1) + WriteBackFingerprint.EarlierSendLife;
        WriteBackFingerprint.MayBeSent(stored, "s1", Now).Should().BeTrue();
        WriteBackFingerprint.MayBeSent(stored, "an upstream edit", lostAt - TimeSpan.FromSeconds(1)).Should().BeTrue();
        WriteBackFingerprint.MayBeSent(stored, "an upstream edit", lostAt).Should().BeFalse();
        SendsOf(stored).Should().BeEquivalentTo(new[] { new[] { $"s{WriteBackFingerprint.MaxEarlierSends + 1}" } });
    }

    [Fact]
    public void SendsNeverConfirmed_KeepTheAcceptedOne_AndTheLatestOfTheRest_AndTheOthersStayEchoes()
    {
        string? stored = "w";
        for (var i = 0; i < WriteBackFingerprint.MaxUnconfirmedSends + 3; i++)
            stored = WriteBackFingerprint.Sending(stored, [$"s{i}"], Now);

        var sends = SendsOf(stored).Select(s => s.Single()).ToList();
        sends.Should().HaveCount(WriteBackFingerprint.MaxUnconfirmedSends + 1);
        sends[0].Should().Be("w");
        sends[^1].Should().Be($"s{WriteBackFingerprint.MaxUnconfirmedSends + 2}");
        WriteBackFingerprint.MayBeSent(stored!, "s0", Now).Should().BeTrue();
    }

    [Theory]
    [InlineData("a|x@b")]
    [InlineData("a|@b")]
    [InlineData("a|b")]
    [InlineData("a|99999999999999999999@b")]
    [InlineData("a|-5@b")]
    [InlineData("a||soon")]
    [InlineData("a|1@b|99999999999999999999")]
    public void AStoredValueThatCannotBeRead_KeepsItsSends_AndMakesEveryCopyAnEchoForOneLife(string stored)
    {
        WriteBackFingerprint.Sends(stored).Should().ContainSingle().Which.Should().Equal("a");
        WriteBackFingerprint.MayBeSent(stored, "an upstream edit", Now).Should().BeTrue();

        var rewritten = WriteBackFingerprint.Accepted(stored, ["c"], Now);
        WriteBackFingerprint.Sends(rewritten).First().Should().Equal("c");
        WriteBackFingerprint.MayBeSent(rewritten, "an upstream edit", Now + WriteBackFingerprint.EarlierSendLife - TimeSpan.FromSeconds(1))
            .Should().BeTrue();
        WriteBackFingerprint.MayBeSent(rewritten, "an upstream edit", Now + WriteBackFingerprint.EarlierSendLife)
            .Should().BeFalse();
    }

    [Fact]
    public void ThePreBolusWriteBackLeavesOut_IsNotFingerprinted()
    {
        var meal = Bolus();
        meal.PreBolus = 15;

        NightscoutTreatmentWriteBackSink.AsPulled(meal).Should().Be(NightscoutTreatmentWriteBackSink.AsPulled(Bolus()));
    }
}
