using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Ids for <c>auth_audit_log</c> rows that sort, as PostgreSQL orders <c>uuid</c>, after every row
/// already staged on the same context, because rows of one commit share <c>created_at</c> and the
/// reader orders them by id.
/// </summary>
internal static class AuditRowIds
{
    /// <summary>The first byte of a version 7 id's random tail that holds no version or variant bits.</summary>
    private const int TailStart = 9;

    /// <summary>A version 7 id greater than every <see cref="AuthAuditLogEntity"/> staged as added on <paramref name="db"/>.</summary>
    /// <remarks>
    /// Reads the tracker with change detection off, so staging a row costs one pass over the tracked
    /// entries and never runs <c>DetectChanges</c> in the middle of a writer's staging.
    /// </remarks>
    public static Guid Next(NocturneDbContext db)
    {
        Guid? last = null;
        var tracker = db.ChangeTracker;
        var detects = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries<AuthAuditLogEntity>())
            {
                if (entry.State == EntityState.Added && (last is not { } max || Compare(entry.Entity.Id, max) > 0))
                    last = entry.Entity.Id;
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detects;
        }

        var fresh = Guid.CreateVersion7();
        if (last is not { } previous || Compare(fresh, previous) > 0)
            return fresh;
        if (Increment(previous) is { } next)
            return next;

        while (Compare(fresh, previous) <= 0)
            fresh = Guid.CreateVersion7();
        return fresh;
    }

    /// <summary>Compares two ids byte by byte in their big-endian form, the order PostgreSQL gives <c>uuid</c>.</summary>
    internal static int Compare(Guid left, Guid right)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        left.TryWriteBytes(a, bigEndian: true, out _);
        right.TryWriteBytes(b, bigEndian: true, out _);
        return a.SequenceCompareTo(b);
    }

    /// <summary>The id one above <paramref name="id"/> in its random tail; null when the tail would overflow.</summary>
    private static Guid? Increment(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        for (var i = bytes.Length - 1; i >= TailStart; i--)
        {
            if (++bytes[i] != 0)
                return new Guid(bytes, bigEndian: true);
        }

        return null;
    }
}
