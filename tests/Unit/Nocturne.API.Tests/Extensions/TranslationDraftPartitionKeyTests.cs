using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// The translation-drafts limiter partitions before AuthenticationMiddleware
/// runs, so the key can only come from the credential the request carries —
/// never from a header the caller can rotate for free.
/// </summary>
public class TranslationDraftPartitionKeyTests
{
    private static readonly OidcOptions Options = new();

    private static DefaultHttpContext Context(
        string? accessToken = null,
        string? refreshToken = null,
        string? authorization = null,
        string? queryToken = null,
        string? forwardedFor = null,
        string remoteIp = "10.0.0.1")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<OidcOptions>>(new OptionsWrapper<OidcOptions>(Options));

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        var cookies = new List<string>();
        if (accessToken is not null)
            cookies.Add($"{Options.Cookie.AccessTokenName}={accessToken}");
        if (refreshToken is not null)
            cookies.Add($"{Options.Cookie.RefreshTokenName}={refreshToken}");
        if (cookies.Count > 0)
            context.Request.Headers.Cookie = string.Join("; ", cookies);
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        if (queryToken is not null)
            context.Request.QueryString = QueryString.Create("token", queryToken);
        if (forwardedFor is not null)
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        return context;
    }

    private static string Key(DefaultHttpContext context) =>
        ServiceRegistrationExtensions.TranslationDraftPartitionKey(context);

    [Fact]
    public void Session_Cookie_Gets_Its_Own_Partition()
    {
        var mine = Key(Context(accessToken: "jwt-a"));
        var theirs = Key(Context(accessToken: "jwt-b"));

        mine.Should().NotBe(theirs);
        mine.Should().Be(Key(Context(accessToken: "jwt-a")));
        mine.Should().NotBe(ServiceRegistrationExtensions.AnonymousDraftPartition);
    }

    [Fact]
    public void Bearer_Token_Gets_Its_Own_Partition()
    {
        var mine = Key(Context(authorization: "Bearer jwt-a"));

        mine.Should().NotBe(Key(Context(authorization: "Bearer jwt-b")));
        mine.Should().NotBe(ServiceRegistrationExtensions.AnonymousDraftPartition);
    }

    [Fact]
    public void Refresh_Token_Carries_The_Partition_When_The_Access_Token_Expired()
    {
        // SessionCookieHandler falls back to the refresh token, so a request
        // that only carries one must still land in a per-session bucket.
        Key(Context(refreshToken: "refresh-a"))
            .Should().NotBe(ServiceRegistrationExtensions.AnonymousDraftPartition);
        Key(Context(refreshToken: "refresh-a"))
            .Should().NotBe(Key(Context(refreshToken: "refresh-b")));
    }

    [Theory]
    [InlineData("Bearer jwt-a")]
    [InlineData("bearer jwt-a")]
    [InlineData("BEARER jwt-a")]
    [InlineData("Bearer  jwt-a")]
    [InlineData("Bearer jwt-a ")]
    [InlineData("Bearer \tjwt-a\t")]
    public void Bearer_Spelling_Variants_Share_One_Partition(string authorization)
    {
        // The token handlers match the scheme case-insensitively and trim the
        // remainder, so all of these authenticate as the same credential. If
        // the raw header were hashed, each spelling would be a fresh 60/min
        // bucket that one authenticated caller could mint without limit.
        Key(Context(authorization: authorization))
            .Should().Be(Key(Context(authorization: "Bearer jwt-a")));
    }

    [Fact]
    public void Non_Bearer_Authorization_Is_Only_Trimmed()
    {
        // Nothing strips a scheme the handlers do not recognise, so the whole
        // value stays the credential — but padding still must not split it.
        Key(Context(authorization: "  Token abc  "))
            .Should().Be(Key(Context(authorization: "Token abc")));
        Key(Context(authorization: "Token abc"))
            .Should().NotBe(Key(Context(authorization: "Bearer abc")));
    }

    [Fact]
    public void Cookie_Takes_Precedence_Over_The_Authorization_Header()
    {
        // The security argument rests on this order: a caller who already has
        // a session cannot escape its bucket by also sending a header, and
        // rotating that header mints nothing.
        var cookieOnly = Key(Context(accessToken: "jwt-a"));

        Key(Context(accessToken: "jwt-a", authorization: "Bearer other-1")).Should().Be(cookieOnly);
        Key(Context(accessToken: "jwt-a", authorization: "Bearer other-2")).Should().Be(cookieOnly);
    }

    [Fact]
    public void Query_Token_Gets_Its_Own_Partition()
    {
        // DirectGrantTokenHandler and AccessTokenHandler both authenticate a
        // Nightscout-style ?token=, so a request carrying only one is an
        // identified caller, not an anonymous flood.
        var mine = Key(Context(queryToken: "noc_grant-a"));

        mine.Should().NotBe(Key(Context(queryToken: "noc_grant-b")));
        mine.Should().NotBe(ServiceRegistrationExtensions.AnonymousDraftPartition);
    }

    [Fact]
    public void Query_Token_Prefix_Variants_Share_One_Partition()
    {
        // DirectGrantTokenHandler normalizes the noc_ marker in on the query
        // path, so both spellings resolve to the same grant — and so must to
        // the same bucket.
        Key(Context(queryToken: "grant-a"))
            .Should().Be(Key(Context(queryToken: "noc_grant-a")));
    }

    [Fact]
    public void Query_Token_Survives_A_Rotating_Unrecognised_Authorization_Header()
    {
        // The bypass this ordering closes: neither token handler reads an
        // Authorization header whose scheme it does not recognise, so the
        // query token still authenticates. Keying on the header would mint a
        // fresh 60/min bucket per rotation for one direct-grant holder.
        var tokenOnly = Key(Context(queryToken: "noc_grant-a"));

        Key(Context(authorization: "Token junk-1", queryToken: "noc_grant-a")).Should().Be(tokenOnly);
        Key(Context(authorization: "Token junk-2", queryToken: "noc_grant-a")).Should().Be(tokenOnly);
        Key(Context(authorization: "Basic junk-3", queryToken: "noc_grant-a")).Should().Be(tokenOnly);
    }

    [Fact]
    public void Bearer_Header_Outranks_The_Query_Token()
    {
        // The handlers read the query only when no Bearer is present, so the
        // Bearer is what authenticates — and a rotating ?token= behind it must
        // not move the bucket.
        var bearerOnly = Key(Context(authorization: "Bearer jwt-a"));

        Key(Context(authorization: "Bearer jwt-a", queryToken: "rot-1")).Should().Be(bearerOnly);
        Key(Context(authorization: "Bearer jwt-a", queryToken: "rot-2")).Should().Be(bearerOnly);
    }

    [Fact]
    public void Session_Cookie_Outranks_The_Query_Token()
    {
        // A cookie SessionCookieHandler cannot validate ends the chain with a
        // Failure, so nothing behind it authenticates: the cookie is always
        // the credential, and a rotating ?token= mints nothing.
        var cookieOnly = Key(Context(accessToken: "jwt-a"));

        Key(Context(accessToken: "jwt-a", queryToken: "rot-1")).Should().Be(cookieOnly);
        Key(Context(accessToken: "jwt-a", queryToken: "rot-2")).Should().Be(cookieOnly);

        var refreshOnly = Key(Context(refreshToken: "refresh-a"));
        Key(Context(refreshToken: "refresh-a", queryToken: "rot-1")).Should().Be(refreshOnly);
        Key(Context(refreshToken: "refresh-a", queryToken: "rot-2")).Should().Be(refreshOnly);
    }

    [Fact]
    public void Credential_Is_Never_The_Key_Itself()
    {
        Key(Context(accessToken: "secret-token")).Should().NotContain("secret-token");
    }

    [Fact]
    public void Key_Does_Not_Derive_From_Caller_Controlled_Headers()
    {
        // Rotating X-Forwarded-For (which UseForwardedHeaders turns into
        // RemoteIpAddress) must not mint a fresh bucket for the same session.
        var first = Key(Context(accessToken: "jwt-a", forwardedFor: "1.2.3.4", remoteIp: "1.2.3.4"));
        var second = Key(Context(accessToken: "jwt-a", forwardedFor: "5.6.7.8", remoteIp: "5.6.7.8"));

        first.Should().Be(second);
    }

    [Fact]
    public void Credentialless_Requests_Share_One_Fixed_Bucket()
    {
        // No per-IP fallback: an anonymous flood rotating X-Forwarded-For gets
        // the same bucket every time rather than an unlimited supply of them.
        Key(Context(forwardedFor: "1.2.3.4", remoteIp: "1.2.3.4"))
            .Should().Be(ServiceRegistrationExtensions.AnonymousDraftPartition);
        Key(Context(forwardedFor: "5.6.7.8", remoteIp: "5.6.7.8"))
            .Should().Be(ServiceRegistrationExtensions.AnonymousDraftPartition);
    }
}
