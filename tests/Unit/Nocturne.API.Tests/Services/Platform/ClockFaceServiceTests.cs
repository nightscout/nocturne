using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

[Trait("Category", "Unit")]
public class ClockFaceServiceTests
{
    private readonly NocturneDbContext _db = TestDbContextFactory.CreateInMemoryContext();
    private readonly ClockFaceService _service;

    public ClockFaceServiceTests()
    {
        _db.TenantId = Guid.NewGuid();
        _service = new ClockFaceService(_db, NullLogger<ClockFaceService>.Instance);
    }

    private Guid AddSubject(string? preferences)
    {
        var id = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity { Id = id, Name = "Creator", Preferences = preferences });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task A_face_created_without_a_config_starts_with_its_creators_preferences()
    {
        var creator = AddSubject("""{ "glucoseUnits": "mmol", "timeFormat": "24" }""");

        var face = await _service.CreateAsync(creator.ToString(), new CreateClockFaceRequest { Name = "Bedside" });
        var stored = await _service.GetByIdAsync(face.Id);

        stored!.Config.Settings.GlucoseUnits.Should().Be("mmol");
        stored.Config.Settings.TimeFormat.Should().Be("24");
        stored.Config.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_creator_with_no_preferences_gets_mgdl_and_12_hour()
    {
        var creator = AddSubject(null);

        var starter = await _service.GetStarterConfigAsync(creator.ToString());

        starter.Settings.GlucoseUnits.Should().Be("mg/dl");
        starter.Settings.TimeFormat.Should().Be("12");
    }

    [Fact]
    public async Task A_face_created_with_a_config_keeps_it()
    {
        var creator = AddSubject("""{ "glucoseUnits": "mmol", "timeFormat": "24" }""");
        var config = new ClockFaceConfig
        {
            Rows = [new ClockRow { Elements = [new ClockElement { Type = "time" }] }],
            Settings = new ClockSettings { GlucoseUnits = "mg/dl", TimeFormat = "12" },
        };

        var face = await _service.CreateAsync(
            creator.ToString(), new CreateClockFaceRequest { Name = "Bedside", Config = config });
        var stored = await _service.GetByIdAsync(face.Id);

        stored!.Config.Settings.GlucoseUnits.Should().Be("mg/dl");
        stored.Config.Rows.Single().Elements.Single().Type.Should().Be("time");
    }
}
