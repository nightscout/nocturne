using System.Text;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Models;

namespace Nocturne.API.Extensions;

public static class EntriesTextExtensions
{
    /// <summary>
    /// The entries in the text layout the <paramref name="format"/> or the request's <c>Accept</c>
    /// header asks for, or <see langword="null"/> when the response is JSON.
    /// </summary>
    public static ActionResult? EntriesText(
        this ControllerBase controller,
        Entry[] entries,
        string? format
    ) =>
        DataFormatService.NegotiateFormat(format, controller.Request.GetTypedHeaders().Accept)
            is { } textFormat
            ? controller.Content(
                DataFormatService.FormatEntries(entries, textFormat),
                DataFormatService.GetContentType(textFormat),
                Encoding.UTF8
            )
            : null;
}
