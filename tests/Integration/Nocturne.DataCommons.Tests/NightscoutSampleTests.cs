using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Xunit;

namespace Nocturne.DataCommons.Tests;

/// <summary>
/// Bulk compatibility tests that deserialize every record from the OpenAPS Data Commons
/// sample dataset and verify Nocturne can ingest it without errors.
///
/// These tests read directly from .data/2023 - nightscout samples.zip and skip
/// gracefully if the file isn't present. Run with:
///   dotnet test --filter "Category=DataCommons"
///
/// Only the 3 Nightscout-format users are tested (86025410, 96254963, 74077367).
/// </summary>
[Trait("Category", "DataCommons")]
public class NightscoutSampleTests
{
    private readonly ITestOutputHelper _output;
    private readonly string? _zipPath;

    public NightscoutSampleTests(ITestOutputHelper output)
    {
        _output = output;
        _zipPath = NightscoutSampleReader.FindZipPath();
    }

    // ========================================================================
    // Entries
    // ========================================================================

    [Fact]
    public void AllEntries_Deserialize()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var totalRecords = 0;
        var totalFiles = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Entry>(_zipPath!, "entries"))
        {
            totalFiles++;
            totalRecords += records.Length;
            _output.WriteLine($"  {fileName}: {records.Length} entries");
        }

        _output.WriteLine($"Total: {totalRecords} entries across {totalFiles} files");
        totalRecords.Should().BeGreaterThan(0, "should find entry records in the dataset");
    }

    [Fact]
    public void AllEntries_HaveRequiredFields()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Entry>(_zipPath!, "entries"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                var entry = records[i];

                // Every sgv entry should have a sensor glucose value
                // mbg entries may store glucose in the unmapped "glucose" field, so we don't check those
                if (entry.Type == "sgv" && entry.Sgv is null or 0)
                {
                    failures.Add($"{fileName}[{i}] _id={entry.Id}: sgv entry with no sgv value");
                }

                // Every entry should have a timestamp
                if (entry.Mills == 0 && string.IsNullOrEmpty(entry.DateString))
                {
                    failures.Add($"{fileName}[{i}] _id={entry.Id}: no timestamp (mills=0, dateString=null)");
                }

                if (failures.Count >= 50)
                {
                    failures.Add("... truncated after 50 failures");
                    break;
                }
            }
            if (failures.Count >= 50) break;
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"Entry field failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all entries should have glucose values and timestamps");
    }

    [Fact]
    public void AllEntries_MapToEntity()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();
        var totalMapped = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Entry>(_zipPath!, "entries"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                try
                {
                    EntryMapper.ToEntity(records[i]);
                    totalMapped++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{fileName}[{i}] _id={records[i].Id}: {ex.GetType().Name}: {ex.Message}");
                    if (failures.Count >= 50) break;
                }
            }
            if (failures.Count >= 50) break;
        }

        _output.WriteLine($"Successfully mapped {totalMapped} entries");

        if (failures.Count > 0)
        {
            _output.WriteLine($"Mapper failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all entries should map to database entities without throwing");
    }

    // ========================================================================
    // Treatments
    // ========================================================================

    [Fact]
    public void AllTreatments_Deserialize()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var totalRecords = 0;
        var totalFiles = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Treatment>(_zipPath!, "treatments"))
        {
            totalFiles++;
            totalRecords += records.Length;
            _output.WriteLine($"  {fileName}: {records.Length} treatments");
        }

        _output.WriteLine($"Total: {totalRecords} treatments across {totalFiles} files");
        totalRecords.Should().BeGreaterThan(0, "should find treatment records in the dataset");
    }

    [Fact]
    public void AllTreatments_HaveRequiredFields()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();
        var eventTypeCounts = new Dictionary<string, int>();

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Treatment>(_zipPath!, "treatments"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                var treatment = records[i];

                // Track eventType distribution
                var eventType = treatment.EventType ?? "(null)";
                eventTypeCounts[eventType] = eventTypeCounts.GetValueOrDefault(eventType) + 1;

                // Every treatment should have a timestamp
                if (string.IsNullOrEmpty(treatment.CreatedAt) && treatment.Mills == 0)
                {
                    failures.Add($"{fileName}[{i}] _id={treatment.Id}: no timestamp (created_at=null, mills=0)");
                }

                if (failures.Count >= 50) break;
            }
            if (failures.Count >= 50) break;
        }

        _output.WriteLine("EventType distribution:");
        foreach (var (type, count) in eventTypeCounts.OrderByDescending(x => x.Value))
        {
            _output.WriteLine($"  {type}: {count}");
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"Treatment field failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all treatments should have timestamps");
    }

    [Fact]
    public void AllTreatments_MapToEntity()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();
        var totalMapped = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Treatment>(_zipPath!, "treatments"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                try
                {
                    TreatmentMapper.ToEntity(records[i]);
                    totalMapped++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{fileName}[{i}] _id={records[i].Id}: {ex.GetType().Name}: {ex.Message}");
                    if (failures.Count >= 50) break;
                }
            }
            if (failures.Count >= 50) break;
        }

        _output.WriteLine($"Successfully mapped {totalMapped} treatments");

        if (failures.Count > 0)
        {
            _output.WriteLine($"Mapper failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all treatments should map to database entities without throwing");
    }

    // ========================================================================
    // Profiles
    // ========================================================================

    [Fact]
    public void AllProfiles_Deserialize()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var totalRecords = 0;
        var totalFiles = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Profile>(_zipPath!, "profile"))
        {
            totalFiles++;
            totalRecords += records.Length;
            _output.WriteLine($"  {fileName}: {records.Length} profiles");
        }

        _output.WriteLine($"Total: {totalRecords} profiles across {totalFiles} files");
        totalRecords.Should().BeGreaterThan(0, "should find profile records in the dataset");
    }

    [Fact]
    public void AllProfiles_HaveRequiredFields()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<Profile>(_zipPath!, "profile"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                var profile = records[i];

                if (profile.Store == null || profile.Store.Count == 0)
                {
                    failures.Add($"{fileName}[{i}] _id={profile.Id}: empty store");
                }

                if (string.IsNullOrEmpty(profile.DefaultProfile))
                {
                    failures.Add($"{fileName}[{i}] _id={profile.Id}: no defaultProfile");
                }

                if (failures.Count >= 50) break;
            }
            if (failures.Count >= 50) break;
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"Profile field failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all profiles should have store and defaultProfile");
    }

    // ========================================================================
    // DeviceStatus
    // ========================================================================

    [Fact]
    public void AllDeviceStatuses_Deserialize()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var totalRecords = 0;
        var totalFiles = 0;

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<DeviceStatus>(_zipPath!, "devicestatus"))
        {
            totalFiles++;
            totalRecords += records.Length;
            _output.WriteLine($"  {fileName}: {records.Length} device statuses");
        }

        _output.WriteLine($"Total: {totalRecords} device statuses across {totalFiles} files");
        totalRecords.Should().BeGreaterThan(0, "should find devicestatus records in the dataset");
    }

    [Fact]
    public void AllDeviceStatuses_HaveRequiredFields()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var failures = new List<string>();

        foreach (var (fileName, records) in NightscoutSampleReader.ReadCollection<DeviceStatus>(_zipPath!, "devicestatus"))
        {
            for (var i = 0; i < records.Length; i++)
            {
                var ds = records[i];

                if (string.IsNullOrEmpty(ds.Device) && string.IsNullOrEmpty(ds.CreatedAt) && ds.Mills == 0)
                {
                    failures.Add($"{fileName}[{i}] _id={ds.Id}: no device, created_at, or mills");
                }

                if (failures.Count >= 50) break;
            }
            if (failures.Count >= 50) break;
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"DeviceStatus field failures ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        failures.Should().BeEmpty("all device statuses should have identifying fields");
    }

    // ========================================================================
    // Summary — runs last, provides aggregate stats
    // ========================================================================

    [Fact]
    public void Summary_AllCollections()
    {
        Assert.SkipWhen(_zipPath == null, "Sample data zip not found in .data/");

        var stats = new Dictionary<string, (int files, int records)>();

        foreach (var collection in new[] { "entries", "treatments", "profile", "devicestatus" })
        {
            var files = 0;
            var records = 0;

            // Use JsonDocument for raw counting to avoid model-specific issues
            foreach (var (fileName, rawRecords) in NightscoutSampleReader.ReadCollection<JsonDocument>(_zipPath!, collection))
            {
                files++;
                records += rawRecords.Length;
            }

            stats[collection] = (files, records);
        }

        _output.WriteLine("=== DataCommons Sample Summary ===");
        foreach (var (collection, (files, records)) in stats)
        {
            _output.WriteLine($"  {collection}: {records:N0} records across {files} files");
        }

        var totalRecords = stats.Values.Sum(s => s.records);
        _output.WriteLine($"  TOTAL: {totalRecords:N0} records");

        totalRecords.Should().BeGreaterThan(0);
    }
}
