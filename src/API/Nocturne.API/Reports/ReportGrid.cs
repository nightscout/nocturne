namespace Nocturne.API.Reports;

/// <summary>
/// A chart panel's grid: the slots its layers' rows fold into, by each field's own
/// <see cref="ReportAggregate"/>. Slots are whole steps from local midnight in the tenant's zone on a
/// time axis and a time-of-day axis (so they stay put from one run to the next and a day's slot starts
/// at its midnight), from zero minutes on an offset axis, and are the days themselves on a day axis.
/// On a time axis a day that a clock change makes 23 or 25 hours long ends in a shorter slot, and
/// the next day's slots start again at its midnight.
/// </summary>
internal static class ReportGrid
{
    /// <summary>The grid a derived series is computed on when the panel gives none, in minutes.</summary>
    public const int DefaultMinutes = 5;

    /// <summary>The most slots one layer folds into; a longer range coarsens the grid to keep within it.</summary>
    public const int MaxSlots = 2000;

    public const int MaxMinutes = 1440;

    /// <summary>The kinds of axis a grid applies to; a day axis has one slot a day and a category axis none.</summary>
    public static bool Applies(ReportXKind kind) => kind is ReportXKind.Time or ReportXKind.Offset or ReportXKind.TimeOfDay;

    /// <summary>
    /// The step, in minutes, the layers of a panel along <paramref name="kind"/> fold at: <paramref name="minutes"/>
    /// coarsened so a time range of <paramref name="from"/> to <paramref name="to"/> holds at most <see cref="MaxSlots"/> slots.
    /// </summary>
    public static int Step(ReportXKind kind, int minutes, DateTimeOffset from, DateTimeOffset to)
    {
        if (kind == ReportXKind.Day)
            return MaxMinutes;
        var step = Math.Max(1, minutes);
        return kind == ReportXKind.Time ? Math.Max(step, (int)Math.Ceiling((to - from).TotalMinutes / MaxSlots)) : step;
    }

    /// <summary>The slot an x value falls in, as the slot's own x value; a time slot in <paramref name="zone"/> (UTC when null).</summary>
    public static long Slot(ReportXKind kind, double x, int step, long fromMills, TimeZoneInfo? zone = null) => kind switch
    {
        ReportXKind.Time => TimeSlot((long)Math.Floor(x), step, zone),
        ReportXKind.Offset or ReportXKind.TimeOfDay => (long)Math.Floor(x / step) * step,
        _ => (long)x,
    };

    /// <summary>The time slot an instant falls in: whole steps from the local midnight before it.</summary>
    internal static long TimeSlot(long mills, int step, TimeZoneInfo? zone)
    {
        var stepMs = step * 60_000L;
        var midnight = Midnight(mills, zone, 0);
        return midnight + (mills - midnight) / stepMs * stepMs;
    }

    /// <summary>The time slot after <paramref name="slot"/>: a step on, or the next local midnight where that comes first.</summary>
    internal static long NextTimeSlot(long slot, int step, TimeZoneInfo? zone) =>
        Math.Min(slot + step * 60_000L, Midnight(slot, zone, 1));

    /// <summary>The instant of local midnight on the day of <paramref name="mills"/>, <paramref name="days"/> days on.</summary>
    private static long Midnight(long mills, TimeZoneInfo? zone, int days)
    {
        var tz = zone ?? TimeZoneInfo.Utc;
        var date = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(mills), tz).Date.AddDays(days);
        var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(15);
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    private static double? Number(object? value) => value switch
    {
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => null,
    };

    private static double? Fold(ReportAggregate aggregate, List<double> values) =>
        values.Count == 0 ? null : aggregate switch
        {
            ReportAggregate.Sum => values.Sum(),
            ReportAggregate.Mean => values.Average(),
            ReportAggregate.Max => values.Max(),
            ReportAggregate.Min => values.Min(),
            ReportAggregate.Last => values[^1],
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, null),
        };

    /// <summary>
    /// The rows folded slot by slot, and by <paramref name="series"/> where given: each of
    /// <paramref name="fields"/> by its aggregate, the series and <paramref name="carried"/> fields from the
    /// slot's first row. Only slots with a row are given; rows outside the range are left out.
    /// </summary>
    public static List<IReadOnlyDictionary<string, object?>> FoldRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string xName,
        IReadOnlyList<ReportField> fields,
        string? series,
        IReadOnlyList<string> carried,
        ReportXKind kind,
        int step,
        long fromMills,
        long toMills,
        TimeZoneInfo? zone = null)
    {
        var groups = new SortedDictionary<(string Series, long Slot), List<IReadOnlyDictionary<string, object?>>>();
        foreach (var row in rows.OrderBy(r => Number(r.GetValueOrDefault(xName)) ?? double.MaxValue))
        {
            if (Number(row.GetValueOrDefault(xName)) is not { } x)
                continue;
            if (kind == ReportXKind.Time && (x < fromMills || x >= toMills))
                continue;
            var key = (series is null ? string.Empty : row.GetValueOrDefault(series)?.ToString() ?? string.Empty, Slot(kind, x, step, fromMills, zone));
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(row);
        }

        var folded = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var ((_, slot), members) in groups)
        {
            var row = new Dictionary<string, object?> { [xName] = slot };
            foreach (var field in fields)
                row[field.Name] = Fold(field.Aggregate!.Value,
                    [.. members.Select(m => Number(m.GetValueOrDefault(field.Name))).OfType<double>()]) is { } v ? Math.Round(v, 4) : null;
            foreach (var name in carried.Append(series).OfType<string>())
                row[name] = members[0].GetValueOrDefault(name);
            folded.Add(row);
        }
        return folded;
    }

    /// <summary>
    /// One field of the rows folded into each slot by its aggregate, keyed by slot. A step field
    /// (<see cref="ReportField.Step"/>, folded by its last value) is not given here for empty slots;
    /// <see cref="Align"/> carries it.
    /// </summary>
    public static Dictionary<long, double?> Values(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string xName,
        ReportField field,
        ReportXKind kind,
        int step,
        long fromMills,
        long toMills,
        TimeZoneInfo? zone = null)
    {
        var slots = new SortedDictionary<long, List<double>>();
        foreach (var row in rows.OrderBy(r => Number(r.GetValueOrDefault(xName)) ?? double.MaxValue))
        {
            if (Number(row.GetValueOrDefault(xName)) is not { } x)
                continue;
            if (kind == ReportXKind.Time && (x < fromMills || x >= toMills))
                continue;
            var slot = Slot(kind, x, step, fromMills, zone);
            if (!slots.TryGetValue(slot, out var list))
                slots[slot] = list = [];
            if (Number(row.GetValueOrDefault(field.Name)) is { } v)
                list.Add(v);
        }
        return slots.ToDictionary(s => s.Key, s => Fold(field.Aggregate!.Value, s.Value));
    }

    /// <summary>
    /// The slots one or two folded series lie on, in order, and each series' value in each: every slot
    /// of the range on a time axis, every slot of the day on a time-of-day axis, every slot from the
    /// first to the last on an offset axis, and the days either series has on a day axis. A step
    /// field folded by its last value is carried into the slots after it that have none; an amount
    /// folded by its sum is 0 in a slot with none inside the range its dataset read, since no bolus
    /// in a slot is a bolus of 0 there.
    /// </summary>
    public static (long[] Keys, double?[][] Series) Align(
        ReportXKind kind,
        int step,
        long fromMills,
        long toMills,
        IReadOnlyList<GridSeries> series,
        TimeZoneInfo? zone = null)
    {
        long[] keys;
        switch (kind)
        {
            case ReportXKind.Time:
            {
                var slots = new List<long>();
                for (var slot = TimeSlot(fromMills, step, zone); slot < toMills; slot = NextTimeSlot(slot, step, zone))
                    slots.Add(slot);
                keys = [.. slots];
                break;
            }
            case ReportXKind.TimeOfDay:
                keys = [.. Enumerable.Range(0, (1440 + step - 1) / step).Select(i => (long)i * step)];
                break;
            case ReportXKind.Offset:
            {
                var all = series.SelectMany(s => s.Values.Keys).ToList();
                keys = all.Count == 0 ? [] : [.. Enumerable.Range(0, (int)((all.Max() - all.Min()) / step) + 1).Select(i => all.Min() + (long)i * step)];
                break;
            }
            default:
                keys = [.. series.SelectMany(s => s.Values.Keys).Distinct().Order()];
                break;
        }

        var aligned = series.Select(s =>
        {
            var values = new double?[keys.Length];
            double? held = null;
            for (var i = 0; i < keys.Length; i++)
            {
                var found = s.Values.TryGetValue(keys[i], out var v) && v is not null;
                if (found)
                    held = v;
                var covered = s.Zeros is { } zeros && (kind != ReportXKind.Time || (keys[i] >= TimeSlot(zeros.From, step, zone) && keys[i] < zeros.To));
                values[i] = found ? v : s.Carry ? held : covered ? 0 : null;
            }
            return values;
        }).ToArray();
        return (keys, aligned);
    }
}

/// <summary>One source folded onto a grid, by slot.</summary>
/// <param name="Carry">A step field folded by its last value: held into the slots after it that have none.</param>
/// <param name="Zeros">An amount folded by its sum: 0 in an empty slot inside this range, Unix milliseconds, that its dataset read.</param>
internal sealed record GridSeries(Dictionary<long, double?> Values, bool Carry = false, (long From, long To)? Zeros = null);
