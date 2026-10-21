using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware;

/// <summary>
/// Records who the request reads as (<see cref="HttpContextExtensions.RecordReader"/>) once
/// <see cref="AuthenticationMiddleware"/> and <see cref="MemberScopeMiddleware"/> have narrowed it, for
/// the SignalR connections the request may open (<see cref="Hubs.HubConnectionReader"/>).
/// </summary>
public class ReaderRecordingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.RecordReader();
        return next(context);
    }
}
