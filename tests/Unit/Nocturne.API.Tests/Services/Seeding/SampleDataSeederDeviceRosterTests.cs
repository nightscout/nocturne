using FluentAssertions;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>The mapper falls back to CGM for a category it cannot parse, so a seeded device must store a member name.</summary>
public class SampleDataSeederDeviceRosterTests
{
    [Theory]
    [InlineData("G7", DeviceCategory.CGM)]
    [InlineData("G6", DeviceCategory.CGM)]
    [InlineData("Omnipod DASH", DeviceCategory.InsulinPump)]
    [InlineData("Contour Next One", DeviceCategory.GlucoseMeter)]
    public void A_seeded_device_reads_back_as_its_category(string model, DeviceCategory expected)
    {
        var seed = SampleDataSeeder.DeviceRoster.Single(d => d.Model == model);

        var device = PatientDeviceMapper.ToDomainModel(SampleDataSeeder.BuildDevice(seed, Guid.NewGuid(), new DateOnly(2026, 3, 15)));

        device.DeviceCategory.Should().Be(expected);
    }

    [Fact]
    public void The_seeded_pump_reads_back_with_its_algorithm()
    {
        var seed = SampleDataSeeder.DeviceRoster.Single(d => d.Model == "Omnipod DASH");

        var device = PatientDeviceMapper.ToDomainModel(SampleDataSeeder.BuildDevice(seed, Guid.NewGuid(), new DateOnly(2026, 3, 15)));

        device.AidAlgorithm.Should().Be(AidAlgorithm.Trio);
    }
}
