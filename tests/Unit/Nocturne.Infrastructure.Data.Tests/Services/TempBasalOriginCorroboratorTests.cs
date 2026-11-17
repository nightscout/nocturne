using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Proposal = Nocturne.Infrastructure.Data.Services.TempBasalOriginCorroborator.Proposal;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// The matching rules of <see cref="TempBasalOriginCorroborator.Judge"/>, and the device rule of
/// <see cref="TempBasalOriginCorroborator.ProposalsFor"/>.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalOriginCorroboratorTests
{
    private static readonly DateTime Start = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Snapshot = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("c0000000-0000-7000-8000-000000000002");

    private static TempBasalOriginCorroborator.Verdict Judge(double? rate, params Proposal[] proposals) =>
        TempBasalOriginCorroborator.Judge(TempBasalOrigin.Algorithm, Start, rate, proposals);

    private static Proposal At(TimeSpan offset, double? enacted = null, double? suggested = null, DateTime? enactedAt = null, Guid? id = null) =>
        new(id ?? Snapshot, Start + offset, enactedAt, enacted, suggested);

    [Fact]
    public void A_snapshot_that_enacted_the_rate_links_it_as_the_loops()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(1), enacted: 1.2))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.Snapshot, Snapshot));
    }

    [Fact]
    public void A_snapshot_that_only_suggested_the_rate_links_it_too()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(-2), enacted: 0.8, suggested: 1.2)).SnapshotId.Should().Be(Snapshot);
    }

    [Theory]
    [InlineData(0.04, true)]
    [InlineData(-0.025, true)]
    [InlineData(0.05, false)]
    [InlineData(0.1, false)]
    public void A_rate_within_a_pump_step_of_the_proposal_is_the_proposal(double off, bool matches)
    {
        Judge(1.2 + off, At(TimeSpan.Zero, suggested: 1.2)).Origin
            .Should().Be(matches ? TempBasalOrigin.Algorithm : TempBasalOrigin.Manual);
    }

    [Theory]
    [InlineData(5 * 60, true)]
    [InlineData(-5 * 60, true)]
    [InlineData(5 * 60 + 1, false)]
    [InlineData(-5 * 60 - 1, false)]
    public void Only_a_snapshot_within_five_minutes_judges_it(int seconds, bool judged)
    {
        var verdict = Judge(1.2, At(TimeSpan.FromSeconds(seconds), enacted: 1.2));

        verdict.Evidence.Should().Be(judged ? TempBasalOriginEvidence.Snapshot : TempBasalOriginEvidence.Uncorroborated);
    }

    [Fact]
    public void An_enactment_within_five_minutes_judges_it_though_its_snapshot_is_later()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(9), enacted: 1.2, enactedAt: Start.AddMinutes(1)))
            .SnapshotId.Should().Be(Snapshot);
    }

    [Fact]
    public void The_nearest_proposing_snapshot_is_linked()
    {
        Judge(1.2,
                At(TimeSpan.FromMinutes(4), enacted: 1.2, id: Other),
                At(TimeSpan.FromMinutes(-1), suggested: 1.2))
            .SnapshotId.Should().Be(Snapshot);
    }

    [Fact]
    public void Snapshots_nearby_that_all_proposed_another_rate_make_it_manual()
    {
        Judge(2.0, At(TimeSpan.FromMinutes(1), enacted: 1.2), At(TimeSpan.FromMinutes(-3), suggested: 0.9))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, null));
    }

    [Fact]
    public void With_no_snapshot_nearby_the_uploader_default_stands()
    {
        TempBasalOriginCorroborator.Judge(TempBasalOrigin.Manual, Start, 1.2, [])
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Uncorroborated, null));
    }

    [Fact]
    public void A_temp_of_unknown_rate_keeps_its_default()
    {
        Judge(null, At(TimeSpan.Zero, enacted: 1.2)).Evidence.Should().Be(TempBasalOriginEvidence.Uncorroborated);
    }

    [Fact]
    public void A_snapshot_that_named_no_rate_is_no_evidence_it_was_set_by_hand()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(1))).Evidence.Should().Be(TempBasalOriginEvidence.Uncorroborated);
    }

    [Fact]
    public void An_enactment_repeated_from_outside_the_window_is_no_evidence()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(1), enacted: 0.5, enactedAt: Start.AddMinutes(-20)))
            .Evidence.Should().Be(TempBasalOriginEvidence.Uncorroborated);
    }

    [Fact]
    public void An_enactment_repeated_from_outside_the_window_does_not_link_it()
    {
        Judge(1.2, At(TimeSpan.FromMinutes(1), enacted: 1.2, suggested: 0.5, enactedAt: Start.AddMinutes(-20)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, null));
    }

    private static TempBasalOriginCorroborator.Verdict JudgeAcrossDevices(
        string? device, double rate, params (string? Device, Proposal Proposal)[] read)
    {
        var (proposals, otherDevice) = TempBasalOriginCorroborator.ProposalsFor(device, Start, read);
        return TempBasalOriginCorroborator.Judge(TempBasalOrigin.Manual, Start, rate, proposals, otherDevice);
    }

    [Fact]
    public void A_temp_with_no_device_is_linked_to_the_one_loop_nearby_as_another_devices()
    {
        JudgeAcrossDevices(null, 1.2, ("openaps://SM-G991B", At(TimeSpan.FromMinutes(1), enacted: 1.2)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.OtherDevice, Snapshot));
    }

    [Fact]
    public void A_temp_mirrored_through_another_uploader_is_linked_to_the_one_loop_nearby_as_another_devices()
    {
        JudgeAcrossDevices("tidepool", 0.85, ("Trio", At(TimeSpan.FromMinutes(-2), enacted: 0.5, suggested: 0.85)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.OtherDevice, Snapshot));
    }

    [Fact]
    public void With_two_loops_nearby_neither_judges_a_temp_of_a_third_device()
    {
        JudgeAcrossDevices(null, 1.2,
                ("openaps://SM-G991B", At(TimeSpan.FromMinutes(1), enacted: 1.2)),
                ("Trio", At(TimeSpan.FromMinutes(2), enacted: 0.4, id: Other)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Uncorroborated, null));
    }

    [Fact]
    public void A_temp_with_no_device_is_not_judged_by_snapshots_with_no_device_beside_a_named_loop()
    {
        JudgeAcrossDevices(null, 1.2,
                (null, At(TimeSpan.FromMinutes(1), enacted: 1.2)),
                ("Trio", At(TimeSpan.FromMinutes(2), enacted: 0.4, id: Other)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Uncorroborated, null));
    }

    [Fact]
    public void A_second_loop_uploading_only_for_a_repeated_enactment_still_counts_as_a_second_loop()
    {
        JudgeAcrossDevices("tidepool", 1.2,
                ("Trio", At(TimeSpan.FromMinutes(1), enacted: 1.2)),
                ("openaps://SM-G991B", At(TimeSpan.FromMinutes(14), id: Other)))
            .Evidence.Should().Be(TempBasalOriginEvidence.Uncorroborated);
    }

    [Fact]
    public void A_loop_uploading_outside_the_window_is_not_a_second_loop()
    {
        JudgeAcrossDevices("tidepool", 1.2,
                ("Trio", At(TimeSpan.FromMinutes(1), enacted: 1.2)),
                ("openaps://SM-G991B", At(TimeSpan.FromMinutes(16), id: Other)))
            .SnapshotId.Should().Be(Snapshot);
    }

    [Fact]
    public void A_temp_whose_device_uploaded_nearby_is_judged_by_its_own_loop_only()
    {
        JudgeAcrossDevices("Trio", 1.2,
                ("Trio", At(TimeSpan.FromMinutes(1), enacted: 0.4)),
                ("openaps://SM-G991B", At(TimeSpan.FromMinutes(-1), enacted: 1.2, id: Other)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.Snapshot, null));
    }

    [Fact]
    public void A_temp_the_one_loop_nearby_proposed_another_rate_for_is_manual()
    {
        JudgeAcrossDevices(null, 2.0, ("openaps://SM-G991B", At(TimeSpan.FromMinutes(1), enacted: 1.2, suggested: 1.2)))
            .Should().Be(new TempBasalOriginCorroborator.Verdict(TempBasalOrigin.Manual, TempBasalOriginEvidence.OtherDevice, null));
    }
}
