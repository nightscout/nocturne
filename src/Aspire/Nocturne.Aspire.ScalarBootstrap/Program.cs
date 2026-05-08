using System.Text;
using Microsoft.Extensions.Primitives;
using Nocturne.Aspire.Scalar;

// Tiny reverse proxy that sits between the Aspire YARP gateway and the
// Scalar.Aspire sidecar. Splices MermaidLazyLoader.HeadContent into the
// Scalar HTML so mermaid code blocks in the OpenAPI description render
// as diagrams. Scalar.Aspire 0.8.x exposes no head-content hook of its
// own, and Aspire.Hosting.Yarp only supports JSON-config transforms
// (header-only — no body rewrite), so we host YARP ourselves.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpForwarderWithServiceDiscovery();

var app = builder.Build();

app.MapDefaultEndpoints();

// HTML rewrite middleware. Forces identity encoding upstream so we can
// read the body, then on the way back splices HeadContent before </head>
// when the response is text/html. Other content types pass through.
app.Use(async (ctx, next) =>
{
    ctx.Request.Headers.AcceptEncoding = new StringValues("identity");

    var originalBody = ctx.Response.Body;
    using var buffer = new MemoryStream();
    ctx.Response.Body = buffer;

    try
    {
        await next();

        ctx.Response.Body = originalBody;
        buffer.Position = 0;

        var contentType = ctx.Response.ContentType ?? string.Empty;
        var isHtml = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        using var reader = new StreamReader(buffer, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();

        const string marker = "</head>";
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var rewritten = idx >= 0
            ? string.Concat(html.AsSpan(0, idx), MermaidLazyLoader.HeadContent, html.AsSpan(idx))
            : html;

        // Rewrite the relative scalar.js reference to an absolute path so
        // the page works whether mounted at /scalar or /scalar/.
        rewritten = rewritten.Replace(
            "<script src=\"scalar.js\"></script>",
            "<script src=\"/scalar/scalar.js\"></script>",
            StringComparison.Ordinal);

        var bytes = Encoding.UTF8.GetBytes(rewritten);
        ctx.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes);
    }
    finally
    {
        ctx.Response.Body = originalBody;
    }
});

app.MapForwarder("/{**catch-all}", "http://scalar");

app.Run();
