using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeArchiveReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<MyLifeEvent> ReadEvents(byte[] zipBytes)
    {
        using var memory = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var results = new List<MyLifeEvent>();

        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var events = JsonSerializer.Deserialize<List<MyLifeEvent>>(json, JsonOptions);
            if (events == null) continue;
            results.AddRange(events);
        }

        return results;
    }
}