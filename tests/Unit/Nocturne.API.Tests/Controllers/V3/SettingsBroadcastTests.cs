using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Effects;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 settings write is announced on the <c>settings</c> storage channel, as upstream's generic
/// collection announces every write. AAPS client control depends on it: a paired client PUTs a
/// signed command under an <c>aaps_clientcontrol_…</c> identifier, and the master acts on the
/// <c>create</c>/<c>update</c> event for it (<c>NsFrameHandler.onSettings</c>); it never polls for
/// commands. The master drops a frame whose document has no <c>srvModified</c>, and routes on the
/// document's <c>identifier</c>.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsBroadcastTests : IDisposable
{
    private const string Command = "aaps_clientcontrol_wizard_c1";
    private const long Date = 1792481008798;

    private readonly NocturneDbContext _context;
    private readonly JsonHubProtocolOptions _protocol = new();
    private readonly List<(string Method, string Group, object Data)> _sent = [];
    private readonly SettingsController _controller;

    public SettingsBroadcastTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext(
            $"v3_settings_broadcast_{Guid.NewGuid()}"
        );
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var broadcast = new SignalRBroadcastService(
            StubHub<DataHub>(),
            StubHub<AlarmHub>(),
            StubHub<ConfigHub>(),
            StubHub<AlertHub>(),
            StubHub<HomeAssistantHub>(),
            StubHub<OverviewHub>(),
            MockTenantAccessor.Create().Object,
            Options.Create(_protocol),
            NullLogger<SignalRBroadcastService>.Instance
        );

        _controller = new SettingsController(
            new SettingsRepository(_context.AsFactory()),
            new WriteSideEffectsService(
                Mock.Of<ITenantCache>(),
                broadcast,
                NullLogger<WriteSideEffectsService>.Instance
            ),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<SettingsController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_put_that_inserts_a_command_is_announced_as_a_create_carrying_the_stored_document()
    {
        await _controller.UpdateSettings(Command, Json(CommandDocument(counter: 1)));

        var (method, doc) = Single();
        method.Should().Be("create");
        doc.GetProperty("identifier").GetString().Should().Be(Command);
        doc.GetProperty("srvModified")
            .GetInt64()
            .Should()
            .BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 60_000);
        doc.GetProperty("app").GetString().Should().Be("AAPS");
        doc.GetProperty("envelope").GetProperty("clientId").GetString().Should().Be("c1");
        doc.GetProperty("envelope").GetProperty("counter").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_put_that_replaces_a_command_is_announced_as_an_update_carrying_the_new_content()
    {
        await _controller.UpdateSettings(Command, Json(CommandDocument(counter: 1)));
        _sent.Clear();

        await _controller.UpdateSettings(Command, Json(CommandDocument(counter: 2)));

        var (method, doc) = Single();
        method.Should().Be("update");
        doc.GetProperty("identifier").GetString().Should().Be(Command);
        doc.TryGetProperty("srvModified", out _).Should().BeTrue();
        doc.GetProperty("envelope").GetProperty("counter").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_post_is_announced_as_a_create()
    {
        await _controller.CreateSettings(
            Json($$"""{"key":"units","value":"mmol","date":{{Date}},"utcOffset":0,"app":"AAPS"}""")
        );

        var (method, doc) = Single();
        method.Should().Be("create");
        doc.GetProperty("key").GetString().Should().Be("units");
    }

    [Fact]
    public async Task A_delete_is_announced_with_the_identifier_of_the_removed_document()
    {
        await _controller.UpdateSettings(Command, Json(CommandDocument(counter: 1)));
        _sent.Clear();

        await _controller.DeleteSettings(Command);

        _sent.Should().ContainSingle();
        var (method, group, data) = _sent[0];
        method.Should().Be("delete");
        var payload = OnWire(method, data);
        payload.GetProperty("colName").GetString().Should().Be("settings");
        payload.GetProperty("identifier").GetString().Should().Be(Command);
        group.Should().Be(SettingsGroup);
    }

    [Fact]
    public async Task A_delete_of_an_unknown_identifier_announces_nothing()
    {
        await _controller.DeleteSettings("aaps_clientcontrol_unknown");

        _sent.Should().BeEmpty();
    }

    private static string SettingsGroup =>
        TenantAwareHub.FormatTenantGroup(MockTenantAccessor.DefaultTenantId.ToString(), "settings");

    /// <summary>
    /// The one event sent, as <see cref="JsonHubProtocol"/> puts it on the wire to the bridge, which
    /// relays it to the <c>/storage</c> sockets of the room named by <c>colName</c>.
    /// </summary>
    private (string Method, JsonElement Doc) Single()
    {
        _sent.Should().ContainSingle();
        var (method, group, data) = _sent[0];
        group.Should().Be(SettingsGroup, "subscribers of the settings collection join this group");
        var payload = OnWire(method, data);
        payload.GetProperty("colName").GetString().Should().Be("settings");
        return (method, payload.GetProperty("doc"));
    }

    private JsonElement OnWire(string method, object data)
    {
        var frame = new JsonHubProtocol(Options.Create(_protocol))
            .GetMessageBytes(new InvocationMessage(method, [data]))
            .ToArray();
        return JsonSerializer
            .Deserialize<JsonElement>(frame.AsSpan(0, frame.Length - 1)) // trailing 0x1e separator
            .GetProperty("arguments")[0];
    }

    private IHubContext<THub> StubHub<THub>()
        where THub : Hub
    {
        var clients = new Mock<IHubClients>();
        clients
            .Setup(c => c.Group(It.IsAny<string>()))
            .Returns(
                (string group) =>
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
                            (method, args, _) => _sent.Add((method, group, args[0]!))
                        )
                        .Returns(Task.CompletedTask);
                    return proxy.Object;
                }
            );

        var hub = new Mock<IHubContext<THub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    /// <summary>A client-control command as AAPS 4.0's <c>ClientControlPublisher</c> writes it.</summary>
    private static string CommandDocument(int counter) =>
        $$$"""{"date":{{{Date}}},"utcOffset":0,"app":"AAPS","envelope":{"clientId":"c1","counter":{{{counter}}},"payload":"e30=","mac":"AA=="}}""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
