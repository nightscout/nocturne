// scripts/dev-smoke.cs
//
// End-to-end smoke test for the local dev stack: proves that a clean
// `aspire start` can produce a browser-ready, logged-in tenant with data.
//
//   1. Dev-only API surface is reachable (Development mode, pinned port)
//   2. seed-tenant creates a tenant with sample data and returns url + loginLink
//   3. The returned bearer token can read AND write tenant data (scope check)
//   4. The loginLink sets real session cookies and redirects into the app
//   5. The cookie session is accepted by the session endpoint via the gateway
//   6. The tenant UI responds on its subdomain through the gateway
//
// Usage:
//   dotnet run scripts/dev-smoke.cs           # seeds, verifies, deletes the tenant
//   dotnet run scripts/dev-smoke.cs --keep    # leave the tenant for inspection
//
// Environment variables (optional):
//   NOCTURNE_API_URL  Direct nocturne-api endpoint (default: http://localhost:1610,
//                     the host port the AppHost pins in run mode)
//
// All hostnames are dialed to loopback (browsers resolve *.localhost themselves;
// the OS resolver may not) and TLS validation is disabled (the gateway may be
// using the ASP.NET dev certificate, which doesn't name tenant subdomains).

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

var keep = args.Contains("--keep");
var apiUrl = Environment.GetEnvironmentVariable("NOCTURNE_API_URL") ?? "http://localhost:1610";
var slug = $"smoke-{Guid.NewGuid():N}"[..12];

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.Error.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures.Add(what);
}

HttpClient LoopbackClient(CookieContainer? cookies = null) => new(new SocketsHttpHandler
{
    CookieContainer = cookies ?? new CookieContainer(),
    UseCookies = true,
    AllowAutoRedirect = true,
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
    },
    ConnectCallback = async (context, ct) =>
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct);
        return new NetworkStream(socket, ownsSocket: true);
    },
});

using var api = LoopbackClient();
api.BaseAddress = new Uri(apiUrl);

// ── 1. Dev-only surface reachable ─────────────────────────────────────────
Console.Error.WriteLine($"Checking dev-only API at {apiUrl} ...");
HttpResponseMessage devCheck;
try
{
    devCheck = await api.GetAsync("/api/v4/dev-only/admin/tenants");
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Cannot reach the API at {apiUrl}: {ex.Message}");
    Console.Error.WriteLine("Is the stack running? Start it with: aspire start");
    return 1;
}
Check(devCheck.StatusCode == HttpStatusCode.OK,
    $"dev-only admin API reachable ({(int)devCheck.StatusCode})"
    + (devCheck.StatusCode == HttpStatusCode.NotFound
        ? " — 404 means the API is not in Development mode"
        : ""));
if (failures.Count > 0) return Fail();

// ── 2. Seed tenant + sample data ──────────────────────────────────────────
Console.Error.WriteLine($"Seeding tenant '{slug}' with sample data ...");
var seedBody = new JsonObject
{
    ["slug"] = slug,
    ["displayName"] = "Smoke Test",
    ["ownerUsername"] = "smoke-owner",
    ["sampleData"] = true,
    ["sampleDataDays"] = 2,
};
var seedResponse = await api.PostAsync("/api/v4/dev-only/admin/seed-tenant",
    new StringContent(seedBody.ToJsonString(), Encoding.UTF8, "application/json"));
var seedJson = JsonNode.Parse(await seedResponse.Content.ReadAsStringAsync())!;
Check(seedResponse.IsSuccessStatusCode, $"seed-tenant succeeded ({(int)seedResponse.StatusCode})");
if (!seedResponse.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"  Response: {seedJson.ToJsonString()}");
    return Fail();
}

var tenantId = seedJson["tenantId"]!.GetValue<Guid>();
var accessToken = seedJson["accessToken"]!.GetValue<string>();
var url = seedJson["url"]?.GetValue<string>();
var loginLink = seedJson["loginLink"]?.GetValue<string>();
var entriesSeeded = seedJson["entriesSeeded"]?.GetValue<int>() ?? 0;
Check(url != null && loginLink != null, $"response carries url + loginLink ({url})");
Check(entriesSeeded > 0, $"sample data seeded ({entriesSeeded} entries)");
if (failures.Count > 0) return Fail();

var tenantHost = new Uri(url!).Host;

// ── 3. Bearer token: read and write tenant data ───────────────────────────
Console.Error.WriteLine("Verifying bearer-token data access ...");
using var bearer = LoopbackClient();
bearer.BaseAddress = new Uri(apiUrl);
bearer.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
bearer.DefaultRequestHeaders.Add("X-Forwarded-Host", tenantHost);

var entriesResponse = await bearer.GetAsync("/api/v1/entries.json?count=10");
var entriesText = await entriesResponse.Content.ReadAsStringAsync();
var entriesArray = entriesResponse.IsSuccessStatusCode ? JsonNode.Parse(entriesText) as JsonArray : null;
Check(entriesArray is { Count: > 0 },
    $"read: GET /api/v1/entries.json returns data ({(int)entriesResponse.StatusCode}, {entriesArray?.Count ?? 0} entries)");

var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var writeBody = new JsonArray(new JsonObject
{
    ["type"] = "sgv",
    ["sgv"] = 123,
    ["date"] = nowMills,
    ["dateString"] = DateTimeOffset.UtcNow.ToString("o"),
    ["direction"] = "Flat",
    ["device"] = "dev-smoke",
});
var writeResponse = await bearer.PostAsync("/api/v1/entries",
    new StringContent(writeBody.ToJsonString(), Encoding.UTF8, "application/json"));
Check(writeResponse.IsSuccessStatusCode,
    $"write: POST /api/v1/entries accepted ({(int)writeResponse.StatusCode}) — proves token scopes");

// ── 4+5. Login link → session cookies → authenticated session ────────────
Console.Error.WriteLine($"Following login link {loginLink} ...");
var cookies = new CookieContainer();
using var browser = LoopbackClient(cookies);
var loginResponse = await browser.GetAsync(loginLink);
var cookieNames = cookies.GetCookies(new Uri(url!)).Select(c => c.Name).ToHashSet();
Check(loginResponse.IsSuccessStatusCode,
    $"login link resolved ({(int)loginResponse.StatusCode} after redirect)");
Check(cookieNames.Contains(".Nocturne.AccessToken") && cookieNames.Contains(".Nocturne.RefreshToken"),
    $"session cookies set ({string.Join(", ", cookieNames)})");

var sessionResponse = await browser.GetAsync($"{url}/api/auth/oidc/session");
Check(sessionResponse.IsSuccessStatusCode,
    $"cookie session accepted by /api/auth/oidc/session via gateway ({(int)sessionResponse.StatusCode})");

// ── 6. Tenant UI on subdomain ─────────────────────────────────────────────
var uiResponse = await browser.GetAsync($"{url}/");
var uiHtml = await uiResponse.Content.ReadAsStringAsync();
Check(uiResponse.IsSuccessStatusCode && uiHtml.Contains("<html", StringComparison.OrdinalIgnoreCase),
    $"tenant UI served on {tenantHost} ({(int)uiResponse.StatusCode})");

// ── Cleanup ───────────────────────────────────────────────────────────────
if (keep)
{
    Console.Error.WriteLine($"Keeping tenant '{slug}' ({tenantId}) — open {loginLink}");
}
else
{
    var deleteResponse = await api.DeleteAsync($"/api/v4/dev-only/admin/tenants/{tenantId}");
    Check(deleteResponse.StatusCode == HttpStatusCode.NoContent,
        $"cleanup: tenant deleted ({(int)deleteResponse.StatusCode})");
}

return Fail();

int Fail()
{
    if (failures.Count == 0)
    {
        Console.Error.WriteLine("SMOKE PASSED");
        return 0;
    }
    Console.Error.WriteLine($"SMOKE FAILED ({failures.Count}):");
    foreach (var f in failures)
        Console.Error.WriteLine($"  - {f}");
    return 1;
}
