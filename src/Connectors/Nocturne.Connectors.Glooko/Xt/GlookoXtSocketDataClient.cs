using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using SocketIOClient.Common;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     <see cref="IGlookoXtDataClient"/> over the real Socket.IO endpoint: websocket transport only,
///     Engine.IO v4, the JWT in the handshake query, no auto-reconnect — a sync opens a session,
///     drains its windows and closes it, and a dropped socket is a failed sync to retry next cycle
///     rather than a background reconnect loop per tenant.
/// </summary>
public class GlookoXtSocketDataClient(ILogger<GlookoXtSocketDataClient> logger) : IGlookoXtDataClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IGlookoXtSession> ConnectAsync(string serverUrl, string token, CancellationToken ct)
    {
        var options = new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            AutoUpgrade = false,
            EIO = EngineIO.V4,
            Reconnection = false,
            ConnectionTimeout = GlookoXtConstants.RequestTimeout,
            Query = new NameValueCollection { ["token"] = token },
        };

        var socket = new SocketIO(new Uri(serverUrl), options);
        var rejected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.OnError += (_, error) => rejected.TrySetResult(error ?? "unknown error");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GlookoXtConstants.RequestTimeout);
            await socket.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new GlookoXtAuthenticationException(
                "Glooko XT did not accept the connection within the time allowed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            throw new GlookoXtAuthenticationException(
                "Glooko XT refused the connection. The stored sign-in may have expired.", ex);
        }

        if (rejected.Task.IsCompleted)
        {
            var error = await rejected.Task;
            socket.Dispose();
            throw new GlookoXtAuthenticationException($"Glooko XT refused the connection: {error}");
        }

        return new Session(socket, logger);
    }

    /// <summary>
    ///     The acknowledgement of <c>GET_COLLECTED_DATA</c> has arrived in three shapes across
    ///     client versions — an object with <c>collected_data</c>, a bare array, and either of
    ///     those serialised into a JSON string — so all three parse to the same list. A shape that
    ///     carries an <c>error</c> throws so the sync fails rather than reads as an empty window.
    /// </summary>
    public static IReadOnlyList<GlookoXtRecord> ParseCollectedData(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text)) return [];
                using (var nested = JsonDocument.Parse(text))
                    return ParseCollectedData(nested.RootElement.Clone());

            case JsonValueKind.Array:
                return element.Deserialize<List<GlookoXtRecord>>(Json) ?? [];

            case JsonValueKind.Object:
                var response = element.Deserialize<GlookoXtCollectedDataResponse>(Json);
                if (response?.Error is { Length: > 0 } error)
                    throw new InvalidOperationException($"Glooko XT answered an error: {error}");
                return response?.CollectedData ?? [];

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return [];

            default:
                throw new InvalidOperationException(
                    $"Glooko XT answered an unexpected {element.ValueKind} to {GlookoXtConstants.Events.GetCollectedData}.");
        }
    }

    /// <summary>The CSV text of an <c>EXPORT_RECORDS</c> answer, in the same three shapes as <see cref="ParseCollectedData"/>; empty when the answer carries none.</summary>
    public static string ParseExportCsv(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                if (text.TrimStart().StartsWith('{'))
                {
                    using var nested = JsonDocument.Parse(text);
                    return ParseExportCsv(nested.RootElement.Clone());
                }
                return text;
            case JsonValueKind.Object:
                if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException($"Glooko XT answered an error: {error.GetString()}");
                return element.TryGetProperty("export", out var export) && export.ValueKind == JsonValueKind.String
                    ? export.GetString() ?? string.Empty
                    : string.Empty;
            default:
                return string.Empty;
        }
    }

    /// <summary>The catalogue entries of a <c>GET_PRODUCTS</c> answer, flattened across brands; same shapes as <see cref="ParseCollectedData"/>.</summary>
    public static IReadOnlyList<GlookoXtProduct> ParseProducts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) return [];
            using var nested = JsonDocument.Parse(text);
            return ParseProducts(nested.RootElement.Clone());
        }

        if (element.ValueKind != JsonValueKind.Object) return [];

        var response = element.Deserialize<GlookoXtProductsResponse>(Json);
        return response?.Brands?
            .SelectMany(b => (b.Products ?? []).Select(p =>
            {
                if (!string.IsNullOrWhiteSpace(b.Name) && !string.IsNullOrWhiteSpace(p.Name)
                    && !p.Name.Contains(b.Name, StringComparison.OrdinalIgnoreCase))
                    p.Name = $"{b.Name} {p.Name}";
                return p;
            }))
            .Where(p => p.Id is not null && !string.IsNullOrWhiteSpace(p.Name))
            .ToList() ?? [];
    }

    /// <summary>ISO-8601 UTC with milliseconds, the exact shape the app formats <c>recorded_at</c> in.</summary>
    public static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Session(SocketIO socket, ILogger logger) : IGlookoXtSession
    {
        public async Task<IReadOnlyList<GlookoXtRecord>> GetCollectedDataAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            var payload = new Dictionary<string, string>
            {
                ["start_time"] = FormatUtc(fromUtc),
                ["end_time"] = FormatUtc(toUtc),
            };

            var element = await EmitAsync(GlookoXtConstants.Events.GetCollectedData, payload, ct);
            var records = ParseCollectedData(element);
            logger.LogDebug("Glooko XT returned {Count} records for {From:O}..{To:O}", records.Count, fromUtc, toUtc);
            return records;
        }

        /// <summary>One request/acknowledgement round trip, bounded by <see cref="GlookoXtConstants.RequestTimeout"/>.</summary>
        private async Task<JsonElement> EmitAsync(string eventName, object payload, CancellationToken ct)
        {
            var ack = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDisconnected(object? _, string reason) =>
                ack.TrySetException(new InvalidOperationException($"Glooko XT closed the connection: {reason}"));

            socket.OnDisconnected += OnDisconnected;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(GlookoXtConstants.RequestTimeout);
                using var registration = timeout.Token.Register(() => ack.TrySetCanceled(timeout.Token));

                await socket.EmitAsync(eventName, [payload], message =>
                {
                    try
                    {
                        ack.TrySetResult(message.GetValue<JsonElement>(0).Clone());
                    }
                    catch (Exception ex)
                    {
                        ack.TrySetException(ex);
                    }

                    return Task.CompletedTask;
                }, timeout.Token);

                return await ack.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Glooko XT did not answer {eventName} within {GlookoXtConstants.RequestTimeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                socket.OnDisconnected -= OnDisconnected;
            }
        }

        public async Task<IReadOnlyList<GlookoXtProduct>> GetProductsAsync(string productType, CancellationToken ct)
        {
            var element = await EmitAsync(GlookoXtConstants.Events.GetProducts,
                new Dictionary<string, string> { ["product_type"] = productType }, ct);
            return ParseProducts(element);
        }

        public async Task<GlookoXtExport> ExportRecordsAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken ct)
        {
            var element = await EmitAsync(GlookoXtConstants.Events.ExportRecords, new Dictionary<string, string>
            {
                ["start_date"] = firstDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["end_date"] = lastDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }, ct);

            var csv = ParseExportCsv(element);
            var export = GlookoXtExportParser.Parse(csv);
            logger.LogDebug("Glooko XT export returned {Count} rows for {From}..{To}", export.Rows.Count, firstDay, lastDay);
            return export;
        }

        public async Task<GlookoXtAccount?> GetAccountAsync(CancellationToken ct)
        {
            var element = await EmitAsync(GlookoXtConstants.Events.GetUserData, new Dictionary<string, string>(), ct);
            return GlookoXtAccount.TryParse(element);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.Connected)
                    await socket.DisconnectAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Glooko XT socket did not close cleanly");
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
