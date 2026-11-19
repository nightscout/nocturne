using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// A stable hash of what a treatment document says upstream, the same whether it is taken from the
/// document Nightscout write-back sent or from the copy the Nightscout connector pulls back. A pulled
/// copy whose fingerprint is one of a send a row keeps (<see cref="Stored"/>, <see cref="MayBeSent"/>)
/// is that send's echo; any other is an edit made upstream.
/// </summary>
/// <remarks>
/// Only the fields write-back sends and an upstream edit can change are read, each through the
/// model's own resolution, so field order, <c>30</c> against <c>30.0</c>, an absent field against a
/// null one, and the format of <c>created_at</c> do not change it. What Nightscout adds or rewrites
/// on a stored document (<c>_id</c>, <c>identifier</c>, <c>utcOffset</c>, <c>srvModified</c>,
/// <c>srvCreated</c>, and the <c>units</c> later releases add to a temporary target) is not read.
/// Nightscout's v1 writes drop a zero or empty field, so a zero, an empty string and a missing value
/// all read alike. <c>isValid</c> counts only when false and <c>automatic</c> only when true, the
/// values that differ from a document that leaves them out.
/// </remarks>
public static class WriteBackFingerprint
{
    public static string Of(Treatment treatment)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            Text(treatment.EventType?.Trim()),
            treatment.Mills.ToString(CultureInfo.InvariantCulture),
            Number(treatment.Insulin),
            Number(treatment.Carbs),
            Number(treatment.Protein),
            Number(treatment.Fat),
            Text(treatment.FoodType),
            Number(treatment.AbsorptionTime),
            Number(treatment.Glucose),
            Text(treatment.GlucoseType),
            Number(treatment.Duration),
            Number(treatment.Absolute),
            Number(treatment.Rate),
            Number(treatment.Percent),
            Text(treatment.Notes),
            Text(treatment.Reason),
            Number(treatment.TargetTop),
            Number(treatment.TargetBottom),
            Text(treatment.Profile),
            Text(treatment.EnteredBy),
            Number(treatment.SplitNow),
            Number(treatment.SplitExt),
            Text(treatment.BolusType),
            treatment.Automatic == true ? "automatic" : null,
            Number(treatment.InsulinDelivered),
            Number(treatment.InsulinProgrammed),
            treatment.IsValid == false ? "invalid" : null,
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Sends a row keeps begun after its confirmed one (<see cref="Sending"/>). Past them the oldest
    /// becomes an earlier send.
    /// </summary>
    public const int MaxUnconfirmedSends = 8;

    /// <summary>
    /// Earlier sends a row keeps (<see cref="Accepted"/>). Past them the oldest is let go before its
    /// <see cref="EarlierSendLife"/> ends, and until then the row reads every pulled document as an
    /// echo (<see cref="MayBeSent"/>).
    /// </summary>
    public const int MaxEarlierSends = 8;

    /// <summary>
    /// How long a send stays an echo once another has taken its place: far longer than the time
    /// between a pull reading a document and planning it, which is about one page of its crawl.
    /// </summary>
    public static readonly TimeSpan EarlierSendLife = TimeSpan.FromHours(1);

    private const char SendSeparator = ';';
    private const char DocumentSeparator = ' ';
    private const char PartSeparator = '|';
    private const char TimeSeparator = '@';

    /// <summary>
    /// What a row stores for one send of its record: the documents it went upstream as. Every row of
    /// the record stores all of them, so each row says on its own whether the record went as a split
    /// meal.
    /// </summary>
    /// <remarks>
    /// A row stores a list of sends: the confirmed one (the last upstream accepted, or the upstream
    /// edit the pull last applied), then those begun after it, oldest first (<see cref="Sends"/>);
    /// then the earlier sends, each with the time another took its place, newest first; then, once an
    /// earlier send was let go before its time, the time until which every pulled document reads as an
    /// echo. Every fingerprint in it is of a document Nocturne sent or began sending, or of the
    /// upstream edit the pull already applied, so a pulled document that matches one is always an
    /// echo: applying it could only revert or remove what Nocturne holds on the strength of what
    /// Nocturne itself sent.
    /// </remarks>
    public static string Stored(IReadOnlyList<string> documents) => string.Join(DocumentSeparator, documents);

    /// <summary>
    /// What a row stores once upstream has accepted its record as <paramref name="documents"/>, from
    /// what it stores (<paramref name="stored"/>, null for nothing): that send as the confirmed one,
    /// then each send begun after it. The confirmed send and those begun before this one become
    /// earlier sends, which a pull that read them before this send can still bring back.
    /// </summary>
    public static string Accepted(string? stored, IReadOnlyList<string> documents, DateTimeOffset now)
    {
        var send = Stored(documents);
        var history = SendHistory.Parse(stored, now);
        if (history.Current.Count == 0)
            return send;

        var at = history.Current.LastIndexOf(send);
        var replaced = at < 0 ? history.Current.Take(1) : history.Current.Take(at);
        var after = at < 0 ? history.Current.Skip(1) : history.Current.Skip(at + 1);
        return (history with { Current = [send, .. after] }).Supersede(replaced, send, now).Format();
    }

    /// <summary>
    /// What a row stores while its record goes upstream as <paramref name="documents"/>, before upstream
    /// answers: what it stores, with this send as the latest begun, so a pull at any moment of the
    /// send reads any document of it as an echo, and one the send fails part way through too. A send
    /// already begun moves to the latest, so accepting it lets go of those begun before it. A row that
    /// stores nothing stays so: only <see cref="Accepted"/> says write-back sent a record.
    /// </summary>
    public static string? Sending(string? stored, IReadOnlyList<string> documents, DateTimeOffset now)
    {
        var history = SendHistory.Parse(stored, now);
        if (history.Current.Count == 0)
            return stored;

        var send = Stored(documents);
        var confirmed = history.Current[0];
        List<string> pending = [.. history.Current.Skip(1).Where(s => s != send)];
        if (send == confirmed && pending.Count == 0)
            return stored;

        pending.Add(send);
        var overflow = Math.Max(pending.Count - MaxUnconfirmedSends, 0);
        var kept = history with { Current = [confirmed, .. pending.Skip(overflow)] };
        return kept.Supersede(pending.Take(overflow), send, now).Format();
    }

    /// <summary>
    /// Whether a pulled document of <paramref name="fingerprint"/> may be one Nocturne sent, by what a
    /// row stores: any document of a send it keeps, or any document at all while an earlier send it
    /// let go before its time could still come back.
    /// </summary>
    public static bool MayBeSent(string stored, string fingerprint, DateTimeOffset now)
    {
        var history = SendHistory.Parse(stored, now);
        return history.LostUntil is not null
            || history.Current.Concat(history.Earlier.Select(e => e.Send))
                .Any(send => send.Split(DocumentSeparator).Contains(fingerprint, StringComparer.Ordinal));
    }

    /// <summary>
    /// Each send upstream may hold by what a row stores, as its documents: the confirmed one first,
    /// then those begun after it. Earlier sends are not among them.
    /// </summary>
    public static IEnumerable<string[]> Sends(string stored) =>
        SendHistory.Parse(stored, now: null).Current
            .Select(send => send.Split(DocumentSeparator, StringSplitOptions.RemoveEmptyEntries));

    /// <param name="Current">The confirmed send, then those begun after it.</param>
    /// <param name="Earlier">Sends another took the place of, newest first, with the Unix second it did.</param>
    /// <param name="LostUntil">The Unix second until which an earlier send let go could still come back.</param>
    private sealed record SendHistory(List<string> Current, List<(long At, string Send)> Earlier, long? LostUntil)
    {
        private static readonly long Life = (long)EarlierSendLife.TotalSeconds;

        /// <summary>
        /// Reads what a row stores. A value whose earlier sends or lost-until time cannot be read
        /// keeps its current sends and reads every pulled document as an echo for one
        /// <see cref="EarlierSendLife"/>, so a damaged value never lets a pulled document be applied.
        /// </summary>
        /// <param name="now">Leaves out what has run its life by then; null keeps everything.</param>
        public static SendHistory Parse(string? stored, DateTimeOffset? now)
        {
            var parts = (stored ?? "").Split(PartSeparator);
            List<string> current = [.. parts[0].Split(SendSeparator, StringSplitOptions.RemoveEmptyEntries)];
            var time = now?.ToUnixTimeSeconds();

            var unreadable = false;
            List<(long At, string Send)> earlier = [];
            if (parts.Length > 1)
            {
                foreach (var entry in parts[1].Split(SendSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = entry.IndexOf(TimeSeparator);
                    if (separator <= 0 || !TryReadSecond(entry[..separator], out var at))
                    {
                        unreadable = true;
                        continue;
                    }

                    if (time is null || at > time - Life)
                        earlier.Add((at, entry[(separator + 1)..]));
                }
            }

            long? lostUntil = null;
            if (parts.Length > 2)
            {
                if (TryReadSecond(parts[2], out var until))
                    lostUntil = until;
                else
                    unreadable = true;
            }

            if (lostUntil <= time)
                lostUntil = null;
            if (unreadable)
                lostUntil = Math.Max(lostUntil ?? 0, (time ?? 0) + Life);

            return new SendHistory(current, earlier, lostUntil);
        }

        private static bool TryReadSecond(string text, out long second) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out second);

        /// <summary>
        /// Makes <paramref name="replaced"/> the newest earlier sends, leaving out <paramref name="kept"/>,
        /// which stays current, and lets go of the oldest past <see cref="MaxEarlierSends"/>.
        /// </summary>
        public SendHistory Supersede(IEnumerable<string> replaced, string kept, DateTimeOffset now)
        {
            var at = now.ToUnixTimeSeconds();
            var newer = replaced.Where(s => s != kept).Distinct(StringComparer.Ordinal).ToList();
            List<(long At, string Send)> earlier =
            [
                .. newer.Select(s => (at, s)),
                .. Earlier.Where(e => e.Send != kept && !newer.Contains(e.Send, StringComparer.Ordinal)),
            ];

            var lostUntil = LostUntil;
            foreach (var (time, _) in earlier.Skip(MaxEarlierSends))
                lostUntil = Math.Max(lostUntil ?? 0, time + Life);

            return new SendHistory(Current, [.. earlier.Take(MaxEarlierSends)], lostUntil);
        }

        public string Format()
        {
            var value = string.Join(SendSeparator, Current);
            if (Earlier.Count == 0 && LostUntil is null)
                return value;

            value += PartSeparator + string.Join(SendSeparator, Earlier.Select(e =>
                e.At.ToString(CultureInfo.InvariantCulture) + TimeSeparator + e.Send));
            return LostUntil is { } until ? value + PartSeparator + until.ToString(CultureInfo.InvariantCulture) : value;
        }
    }

    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? Number(double? value) =>
        value is { } number && number != 0 ? number.ToString("R", CultureInfo.InvariantCulture) : null;
}
