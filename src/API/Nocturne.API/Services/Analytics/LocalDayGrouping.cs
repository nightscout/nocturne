using System.Linq.Expressions;

namespace Nocturne.API.Services.Analytics;

internal static class LocalDayGrouping
{
    // .NET owns the report's timezone rules. PostgreSQL's timezone database can disagree,
    // particularly for historical dates, so SQL receives the same UTC day boundaries.
    internal static Expression<Func<DateTime, DateTime>>? Create(int year, TimeZoneInfo timezone)
    {
        var boundaries = new List<(DateTime Utc, DateTime Day)>();
        var end = new DateTime(year + 1, 1, 1);
        for (var day = new DateTime(year, 1, 1); day <= end; day = day.AddDays(1))
        {
            if (timezone.IsInvalidTime(day) || timezone.IsAmbiguousTime(day))
                return null;

            var utc = TimeZoneInfo.ConvertTimeToUtc(day, timezone);
            if (boundaries.Count > 0 && utc <= boundaries[^1].Utc)
                return null;

            boundaries.Add((utc, DateTime.SpecifyKind(day, DateTimeKind.Utc)));
        }

        var timestamp = Expression.Parameter(typeof(DateTime), "timestamp");
        return Expression.Lambda<Func<DateTime, DateTime>>(
            Partition(0, boundaries.Count - 2), timestamp);

        Expression Partition(int first, int last)
        {
            var offset = boundaries[first].Day - boundaries[first].Utc;
            if (Enumerable.Range(first, last - first + 2)
                .All(index => boundaries[index].Day - boundaries[index].Utc == offset))
            {
                var local = offset == TimeSpan.Zero
                    ? (Expression)timestamp
                    : Expression.Call(timestamp, nameof(DateTime.AddSeconds), Type.EmptyTypes,
                        Expression.Constant(offset.TotalSeconds));
                return Expression.Property(local, nameof(DateTime.Date));
            }

            if (first == last)
                return Expression.Constant(boundaries[first].Day);

            var middle = (first + last) / 2;
            return Expression.Condition(
                Expression.LessThan(timestamp, Expression.Constant(boundaries[middle + 1].Utc)),
                Partition(first, middle),
                Partition(middle + 1, last));
        }
    }
}
