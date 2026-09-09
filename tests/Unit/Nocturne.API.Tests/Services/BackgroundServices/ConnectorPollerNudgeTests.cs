using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.BackgroundServices;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorPollerNudgeTests
{
    [Fact]
    public void Invalidate_ReachesEverySubscriberOfThatConnector_CaseInsensitively()
    {
        var nudge = new ConnectorPollerNudge();
        var tenant = Guid.CreateVersion7();
        var seen = new List<(string Poller, Guid Tenant)>();
        nudge.Subscribe("Glooko", id => seen.Add(("glooko-a", id)));
        nudge.Subscribe("Glooko", id => seen.Add(("glooko-b", id)));
        nudge.Subscribe("Dexcom", id => seen.Add(("dexcom", id)));

        nudge.Invalidate("glooko", tenant);

        seen.Should().BeEquivalentTo([("glooko-a", tenant), ("glooko-b", tenant)]);
    }

    [Fact]
    public void Invalidate_ForAConnectorNobodyPolls_IsANoOp()
    {
        var nudge = new ConnectorPollerNudge();

        var act = () => nudge.Invalidate("nobody", Guid.CreateVersion7());

        act.Should().NotThrow();
    }
}
