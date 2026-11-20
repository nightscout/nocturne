
namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Opens Socket.IO sessions against Glooko XT. Behind an interface so the sync can be tested
///     without a socket, and so a session is opened once per sync rather than once per chunk.
/// </summary>
public interface IGlookoXtDataClient
{
    /// <summary>
    ///     Connects with <paramref name="token"/> on the handshake. Throws
    ///     <see cref="GlookoXtAuthenticationException"/> when the server refuses the token.
    /// </summary>
    Task<IGlookoXtSession> ConnectAsync(string serverUrl, string token, CancellationToken ct);
}

/// <summary>One connected Socket.IO session.</summary>
public interface IGlookoXtSession : IAsyncDisposable
{
    /// <summary>Every record whose <c>recorded_at</c> lies in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>].</summary>
    Task<IReadOnlyList<GlookoXtRecord>> GetCollectedDataAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>The device catalogue for one <paramref name="productType"/> (<c>cgm</c>, <c>pump</c>, <c>bgm</c>, <c>pen</c>).</summary>
    Task<IReadOnlyList<GlookoXtProduct>> GetProductsAsync(string productType, CancellationToken ct);

    /// <summary>
    ///     The CSV export for the days <paramref name="firstDay"/> through <paramref name="lastDay"/>,
    ///     inclusive, in the account's zone. See <see cref="GlookoXtExport"/> for why it is read.
    /// </summary>
    Task<GlookoXtExport> ExportRecordsAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken ct);

    /// <summary>The account's glucose unit and zone; null when the profile could not be read.</summary>
    Task<GlookoXtAccount?> GetAccountAsync(CancellationToken ct);
}

/// <summary>The server would not open a session for the presented token.</summary>
public class GlookoXtAuthenticationException(string message, Exception? inner = null) : Exception(message, inner);
