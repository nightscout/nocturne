using System.Net;
using static System.Net.WebUtility;

namespace Nocturne.Services.SelfHostSetup;

public static class SetupPage
{
    public static IResult Render(IPAddress publicIp, SetupOutcome? outcome)
    {
        var body = outcome switch
        {
            SetupOutcome.Completed completed => Starting(completed.BaseDomain),
            SetupOutcome.AlreadyComplete => Message("Setup is already complete. Nocturne is starting."),
            SetupOutcome.LockedOut => Message(
                "Too many wrong instance IDs. Restart this machine from your cloud console to try again."),
            _ => Notice(outcome, publicIp) + Forms(),
        };

        var status = outcome switch
        {
            SetupOutcome.ClaimRejected or SetupOutcome.Invalid => StatusCodes.Status400BadRequest,
            SetupOutcome.LockedOut => StatusCodes.Status429TooManyRequests,
            SetupOutcome.DuckDnsFailed => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status200OK,
        };

        return Results.Content(Layout(body), "text/html; charset=utf-8", statusCode: status);
    }

    private static string Notice(SetupOutcome? outcome, IPAddress publicIp) => outcome switch
    {
        SetupOutcome.ClaimRejected => Error(
            $"That instance ID does not match this machine. {ClaimGuard.MaxFailures} wrong attempts lock setup until a restart."),
        SetupOutcome.Invalid invalid => Error(HtmlEncode(invalid.Message)),
        SetupOutcome.DuckDnsFailed { Reason: DuckDnsOutcome.Unreachable } => Error(
            "Could not reach duckdns.org. Try again in a minute."),
        SetupOutcome.DuckDnsFailed => Error(
            "DuckDNS refused the update. Check the token, and that the name is listed under your domains on duckdns.org."),
        SetupOutcome.DnsPending pending => Error(
            $"""
            The DNS records for {HtmlEncode(pending.Domain)} do not point here yet. Create these A records, wait a
            few minutes, then submit again:
            <table>
              <tr><th>Name</th><th>Value</th><th></th></tr>
              <tr><td><code>{HtmlEncode(pending.Domain)}</code></td><td><code>{pending.PublicIp}</code></td><td>{Tick(pending.ApexReady)}</td></tr>
              <tr><td><code>*.{HtmlEncode(pending.Domain)}</code></td><td><code>{pending.PublicIp}</code></td><td>{Tick(pending.WildcardReady)}</td></tr>
            </table>
            """),
        _ => $"<p class=\"muted\">This machine's public address is <code>{publicIp}</code>.</p>",
    };

    private static string Forms() =>
        """
        <section>
          <h2>Free hostname from DuckDNS</h2>
          <p>Sign in at <a href="https://www.duckdns.org" target="_blank" rel="noopener">duckdns.org</a>, add a
          name, and copy the token shown at the top of the page. The name follows this machine if its address
          ever changes.</p>
          <form method="post" action="/duckdns">
            <label>Instance ID <input name="claim" required autocomplete="off"></label>
            <label>DuckDNS name <input name="name" required placeholder="myname" autocomplete="off"></label>
            <label>DuckDNS token <input name="token" required autocomplete="off"></label>
            <button type="submit">Use DuckDNS</button>
          </form>
        </section>
        <section>
          <h2>Your own domain</h2>
          <p>Point the hostname and a wildcard below it at this machine; setup checks both before it continues.</p>
          <form method="post" action="/domain">
            <label>Instance ID <input name="claim" required autocomplete="off"></label>
            <label>Hostname <input name="domain" required placeholder="nocturne.example.com" autocomplete="off"></label>
            <button type="submit">Use this domain</button>
          </form>
        </section>
        <p class="muted">The instance ID is shown for this machine in your cloud provider's console. It proves you
        own it. The hostname cannot be changed later without registering your passkeys again.</p>
        """;

    private static string Starting(string baseDomain)
    {
        var url = $"https://{baseDomain}/";
        return $"""
            <h2>Nocturne is starting</h2>
            <p>It takes a minute or two to start and fetch its HTTPS certificate. Then open
            <a href="{HtmlEncode(url)}">{HtmlEncode(url)}</a> to create your account.</p>
            """;
    }

    private static string Message(string text) => $"<p>{HtmlEncode(text)}</p>";

    private static string Error(string html) => $"<div class=\"error\">{html}</div>";

    private static string Tick(bool ready) => ready ? "found" : "missing";

    private static string Layout(string body) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Set up Nocturne</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 40rem; margin: 3rem auto; padding: 0 1rem; color: #1f2937; }
            section { border: 1px solid #e5e7eb; border-radius: .5rem; padding: 1rem 1.25rem; margin: 1.5rem 0; }
            label { display: block; margin: .75rem 0; font-weight: 500; }
            input { display: block; width: 100%; box-sizing: border-box; padding: .5rem; margin-top: .25rem; font: inherit; }
            button { padding: .5rem 1rem; font: inherit; cursor: pointer; }
            .error { background: #fef2f2; border: 1px solid #fecaca; padding: .75rem 1rem; border-radius: .5rem; }
            .muted { color: #6b7280; }
            td, th { padding: .25rem .75rem .25rem 0; text-align: left; }
          </style>
        </head>
        <body>
          <h1>Set up Nocturne</h1>
          {{body}}
        </body>
        </html>
        """;
}
