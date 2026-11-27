using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Realtime;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Runs the producers against a real <see cref="SignalRBroadcastService"/> and reads the events
/// back as <see cref="JsonHubProtocol"/> writes them. Asserting on anything else — a fresh
/// <see cref="JsonSerializer"/> call over the captured object, say — validates a serialization
/// no client ever sees.
/// </summary>
internal sealed class StorageBroadcastCapture
{
    private readonly JsonHubProtocolOptions _protocol = new();
    private readonly Dictionary<string, (string Group, object Data)> _sent = [];

    public ISignalRBroadcastService Broadcast { get; }

    /// <param name="configurePayloadSerializer">
    /// Reconfigures the serializer the hubs send payloads with, as an app calling
    /// <c>AddJsonProtocol</c> would.
    /// </param>
    public StorageBroadcastCapture(Action<JsonSerializerOptions>? configurePayloadSerializer = null)
    {
        configurePayloadSerializer?.Invoke(_protocol.PayloadSerializerOptions);

        Broadcast = new SignalRBroadcastService(
            StubHub<DataHub>(),
            StubHub<AlarmHub>(),
            StubHub<ConfigHub>(),
            StubHub<AlertHub>(),
            StubHub<HomeAssistantHub>(),
            StubHub<OverviewHub>(),
            SettableTenantAccessor.Resolved(),
            Options.Create(_protocol),
            NullLogger<SignalRBroadcastService>.Instance
        );
    }

    public JsonElement Delete => Wire("delete");

    public JsonElement Create => Wire("create");

    public JsonElement Update => Wire("update");

    private IHubContext<THub> StubHub<THub>()
        where THub : Hub
    {
        var clients = new Mock<IHubClients>();
        clients
            .Setup(c => c.Group(It.IsAny<string>()))
            .Returns((string group) =>
            {
                var proxy = new Mock<IClientProxy>();
                proxy
                    .Setup(p =>
                        p.SendCoreAsync(
                            It.IsAny<string>(),
                            It.IsAny<object?[]>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Callback<string, object?[], CancellationToken>(
                        (method, args, _) => _sent[method] = (group, args[0]!)
                    )
                    .Returns(Task.CompletedTask);
                return proxy.Object;
            });

        var hub = new Mock<IHubContext<THub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    /// <summary>
    /// The event's bytes on the wire. The first argument of the hub invocation selects the
    /// SignalR tenant group; the bridge picks the socket.io room off the payload's
    /// <c>colName</c>. If the two disagree the event is delivered to a room no client of that
    /// collection has joined.
    /// </summary>
    private JsonElement Wire(string method)
    {
        _sent.ContainsKey(method).Should().BeTrue($"the producer must broadcast a {method} event");
        var (group, data) = _sent[method];

        var frame = new JsonHubProtocol(Options.Create(_protocol))
            .GetMessageBytes(new InvocationMessage(method, [data]))
            .ToArray();
        var payload = JsonSerializer
            .Deserialize<JsonElement>(frame.AsSpan(0, frame.Length - 1)) // trailing 0x1e separator
            .GetProperty("arguments")[0];

        group
            .Should()
            .Be(
                TenantAwareHub.FormatTenantGroup(
                    SettableTenantAccessor.DefaultTenantId.ToString(),
                    payload.GetProperty("colName").GetString()!
                ),
                "the SignalR group and the payload's colName select the same audience and must agree"
            );
        return payload;
    }

    public static string? Identifier(JsonElement payload) =>
        payload.GetProperty("identifier").GetString();

    /// <summary>The keys a client could take for the record's own id, under the hub's naming.</summary>
    public static Dictionary<string, string?> IdKeys(JsonElement doc) =>
        doc.EnumerateObject()
            .Where(p => p.Name is "_id" or "id" or "Id" or "identifier" or "Identifier")
            .ToDictionary(p => p.Name, p => p.Value.GetString());

    /// <summary>A REST projection as the API writes it, under ASP.NET Core's web defaults.</summary>
    public static JsonElement Rest(object projection) =>
        JsonSerializer.SerializeToElement(projection, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static string? RestIdentifier(object projection) =>
        Rest(projection).GetProperty("identifier").GetString();
}
