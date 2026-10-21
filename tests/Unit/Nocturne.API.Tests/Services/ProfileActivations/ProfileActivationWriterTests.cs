using FluentAssertions;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>The ends and links <see cref="ProfileActivationWriter.Settle"/> derives for one written activation.</summary>
public class ProfileActivationWriterTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static ProfileActivation Activation(
        int hour,
        ProfileActivationPhase phase = ProfileActivationPhase.Effective,
        long? durationMs = null,
        int? endHour = null,
        string? dataSource = "glooko-connector") => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = T0.AddHours(hour),
        EndTimestamp = endHour is { } end ? T0.AddHours(end) : null,
        RequestedDurationMs = durationMs,
        Phase = phase,
        BaseProfileName = "A",
        DataSource = dataSource,
    };

    [Fact]
    public void AConnectorsReportedEndStandsWhenNothingLaterStarted()
    {
        var written = Activation(0, endHour: 2);

        var settled = ProfileActivationWriter.Settle(written, [], [], []);

        settled.Ends[written.Id].Should().Be(T0.AddHours(2));
    }

    [Fact]
    public void ATemporaryEffectiveActivationLeavesThePermanentOneRunning()
    {
        var permanent = Activation(0);
        var temporary = Activation(1, durationMs: 3_600_000, endHour: 2);

        var settled = ProfileActivationWriter.Settle(temporary, [permanent], [], []);

        settled.Ends.Should().NotContainKey(permanent.Id);
        settled.Ends[temporary.Id].Should().Be(T0.AddHours(2));
    }

    [Fact]
    public void AnActivationOfAnotherSourceIsNeverEndedOrLinked()
    {
        var other = Activation(0, phase: ProfileActivationPhase.Requested, dataSource: "nightscout-connector");
        var written = Activation(1);

        var settled = ProfileActivationWriter.Settle(written, [other], [], [other]);

        settled.Ends.Keys.Should().Equal(written.Id);
        settled.Confirms.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<Guid, Guid?>(written.Id, null), "it confirms no request of its own source");
    }

    [Fact]
    public void ARequestedActivationIsNotConfirmedByAnEffectiveOneAfterTheNextRequest()
    {
        var written = Activation(0, phase: ProfileActivationPhase.Requested, dataSource: "aaps");
        var nextRequest = Activation(2, phase: ProfileActivationPhase.Requested, dataSource: "aaps");
        nextRequest.BaseProfileName = "B";
        var confirmsNext = Activation(3, dataSource: "aaps");

        var settled = ProfileActivationWriter.Settle(written, [], [nextRequest, confirmsNext], []);

        settled.Confirms.Should().BeEmpty();
        settled.Ends[written.Id].Should().Be(T0.AddHours(2), "the next permanent request ends it");
    }
}
