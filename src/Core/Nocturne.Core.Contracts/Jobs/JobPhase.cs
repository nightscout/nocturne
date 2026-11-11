using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Core.Contracts.Jobs;

/// <summary>
/// A per-tenant row's fixed offset within its job's <see cref="IScheduledJob.Spread"/>: the rows of one
/// job are first due spread over the window rather than together, and each row's offset is the same in
/// every process and after every restart.
/// </summary>
/// <remarks>
/// The first 32 bits of <c>sha256("&lt;job&gt;:&lt;tenant id&gt;")</c>, scaled into the window in whole
/// milliseconds. <c>JobLedgerRepository</c> works out the same offset in SQL when it enrols the rows.
/// </remarks>
public static class JobPhase
{
    public static TimeSpan Of(string job, Guid tenantId, TimeSpan spread)
    {
        var spreadMs = (long)spread.TotalMilliseconds;
        if (spreadMs <= 0)
            return TimeSpan.Zero;

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{job}:{tenantId:D}"), hash);
        long bits = BinaryPrimitives.ReadUInt32BigEndian(hash);
        return TimeSpan.FromMilliseconds(bits * spreadMs / 0x1_0000_0000L);
    }
}
