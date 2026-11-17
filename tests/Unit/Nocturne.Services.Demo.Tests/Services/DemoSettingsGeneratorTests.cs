using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.Services.Demo.Tests.Services;

public class DemoSettingsGeneratorTests
{
    [Fact]
    public void Last_sync_times_are_offsets_from_the_injected_clock()
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var generator = new DemoSettingsGenerator(new DemoModeConfiguration(), new FakeTimeProvider(now));

        var devices = generator.GenerateSettings().Devices!.ConnectedDevices!;

        devices.Select(d => d.LastSync).Should().Equal(now.AddMinutes(-5), now.AddMinutes(-2));
    }
}
