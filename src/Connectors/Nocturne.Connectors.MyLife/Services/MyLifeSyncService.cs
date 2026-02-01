using Microsoft.Extensions.Logging;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeSyncService(MyLifeSoapClient soapClient, ILogger<MyLifeSyncService> logger)
{
    public async Task<IReadOnlyList<MyLifeEvent>> FetchEventsAsync(
        string serviceUrl,
        string authToken,
        string patientId,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken)
    {
        var months = BuildMonths(since, until);
        logger.LogInformation(
            "MyLife fetching months: [{Months}] (since={Since:yyyy-MM-dd}, until={Until:yyyy-MM-dd})",
            string.Join(", ", months), since, until);

        var results = new List<MyLifeEvent>();
        foreach (var month in months)
        {
            logger.LogDebug("MyLife fetching month {Month}", month);

            var encrypted = await soapClient.SyncEventsAsync(
                serviceUrl,
                patientId,
                authToken,
                month,
                cancellationToken
            );

            if (string.IsNullOrWhiteSpace(encrypted))
            {
                logger.LogDebug("MyLife month {Month} returned empty", month);
                continue;
            }

            var decrypted = MyLifeDecryptor.Decrypt(encrypted);
            if (!IsZip(decrypted))
            {
                logger.LogWarning("MyLife month {Month} decrypted data is not a ZIP", month);
                continue;
            }

            var events = MyLifeArchiveReader.ReadEvents(decrypted);
            logger.LogInformation("MyLife month {Month} returned {Count} events", month, events.Count);
            results.AddRange(events);
        }

        logger.LogInformation("MyLife total events fetched: {Count}", results.Count);
        return results;
    }

    private static List<string> BuildMonths(DateTime since, DateTime now)
    {
        var months = new List<string>();
        var current = new DateTime(since.Year, since.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        while (current <= end)
        {
            months.Add(current.ToString("yyyyMM"));
            current = current.AddMonths(1);
        }

        return months;
    }

    private static bool IsZip(byte[] data)
    {
        if (data.Length < 2) return false;

        return data[0] == (byte)'P' && data[1] == (byte)'K';
    }
}