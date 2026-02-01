using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeSyncService(
    MyLifeSoapClient soapClient,
    MyLifeArchiveReader archiveReader)
{
    public async Task<IReadOnlyList<MyLifeEvent>> FetchEventsAsync(
        string serviceUrl,
        string authToken,
        string patientId,
        DateTime since,
        int maxMonths,
        CancellationToken cancellationToken)
    {
        var months = BuildMonths(since, DateTime.UtcNow);
        if (maxMonths > 0 && months.Count > maxMonths) months = months.Skip(months.Count - maxMonths).ToList();

        var results = new List<MyLifeEvent>();
        foreach (var month in months)
        {
            var encrypted = await soapClient.SyncEventsAsync(
                serviceUrl,
                patientId,
                authToken,
                month,
                cancellationToken
            );

            if (string.IsNullOrWhiteSpace(encrypted)) continue;

            var decrypted = MyLifeDecryptor.Decrypt(encrypted);
            if (!IsZip(decrypted)) continue;

            var events = archiveReader.ReadEvents(decrypted);
            results.AddRange(events);
        }

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