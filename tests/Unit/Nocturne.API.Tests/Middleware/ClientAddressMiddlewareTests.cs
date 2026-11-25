using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Constants;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// An audit row records the client the request was made for, not the container that relayed it.
/// </summary>
[Trait("Category", "Unit")]
public class ClientAddressMiddlewareTests
{
    private const string InstanceKey = "s3cret-instance-key";
    private const string WebContainer = "172.30.0.4";
    private const string Browser = "203.0.113.4";

    [Fact]
    public async Task ASignedAddress_IsTheAddressTheAuditContextRecords()
    {
        var auditContext = await AuditedAsync(RequestFrom(Browser, Sign(Browser, InstanceKey)));

        auditContext.IpAddress.Should().Be(Browser);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged")]
    public async Task AnAddressThatIsNotSigned_IsNotRecorded(string? signature)
    {
        var auditContext = await AuditedAsync(RequestFrom(Browser, signature));

        auditContext.IpAddress.Should().Be(WebContainer);
    }

    [Fact]
    public async Task AnAddressSignedWithAnotherKey_IsNotRecorded()
    {
        var auditContext = await AuditedAsync(RequestFrom(Browser, Sign(Browser, "not-the-instance-key")));

        auditContext.IpAddress.Should().Be(WebContainer);
    }

    [Fact]
    public async Task ARecorderBeforeTheAuditContext_ReadsTheResolvedAddress()
    {
        var context = RequestFrom(Browser, Sign(Browser, InstanceKey));

        await new ClientAddressMiddleware(_ => Task.CompletedTask, Resolver()).InvokeAsync(context);

        context.GetClientAddress().Should().Be(Browser);
    }

    [Fact]
    public void WithoutTheMiddleware_ARecorderNeverReadsTheHeader()
    {
        var context = RequestFrom(Browser, Sign(Browser, InstanceKey));

        context.GetClientAddress().Should().Be(WebContainer);
    }

    private static async Task<AuditContext> AuditedAsync(HttpContext context)
    {
        var auditContext = new AuditContext();
        var audit = new AuditContextMiddleware(_ => Task.CompletedTask);

        await new ClientAddressMiddleware(
                ctx => audit.InvokeAsync(ctx, auditContext, CredentialRouteRedactor.Create()),
                Resolver())
            .InvokeAsync(context);

        return auditContext;
    }

    private static ClientAddressResolver Resolver() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = InstanceKey,
            })
            .Build());

    private static DefaultHttpContext RequestFrom(string clientAddress, string? signature)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(WebContainer);
        context.Request.Method = "POST";
        context.Request.Path = "/api/v4/auth/passkey/login/complete";
        context.Request.Headers[ServiceNames.Headers.ClientIp] = clientAddress;
        if (signature is not null)
            context.Request.Headers[ServiceNames.Headers.ClientIpSignature] = signature;
        return context;
    }

    private static string Sign(string value, string instanceKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(instanceKey),
            Encoding.UTF8.GetBytes(value)));
}
