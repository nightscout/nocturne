using FluentAssertions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A subsystem that reads its clock through an injected <see cref="TimeProvider"/> never reads a
/// static clock and never reaches for <c>TimeProvider.System</c>.
/// The API project is too large for a project-wide ban, so each migrated subsystem's folder is listed
/// in <see cref="Folders"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ClockReadGuardTests
{
    private static readonly string[] Folders =
    [
        "src/API/Nocturne.API/Services/Seeding",
        "src/API/Nocturne.API/Services/Demo",
        "src/API/Nocturne.API/Services/Sleep",
        "src/API/Nocturne.API/Services/Treatments",
        "src/API/Nocturne.API/Services/Glucose",
        "src/API/Nocturne.API/Services/Profiles",
        "src/API/Nocturne.API/Services/Timezones",
        "src/API/Nocturne.API/Services/Devices",
        "src/API/Nocturne.API/Services/Monitoring",
        "src/API/Nocturne.API/Services/BackgroundServices",
        "src/API/Nocturne.API/Services/Migration",
        "src/API/Nocturne.API/Services/Alerts",
    ];

    [Fact]
    public void A_migrated_folder_reads_no_static_clock()
    {
        StaticClockReads.InFolders(Folders).Select(r => r.ToString()).Should()
            .BeEmpty("these read a static clock or the system clock; inject TimeProvider instead");
    }

    [Theory]
    [InlineData("class C { void M() { var t = DateTime.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = System.DateTime.Now; } }", 1)]
    [InlineData("class C { void M() { var t = global::System.DateTime.Now; } }", 1)]
    [InlineData("class C { void M() { var t = DateTimeOffset.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = DateTimeOffset.Now; } }", 1)]
    [InlineData("class C { void M() { var t = DateTime.Today; } }", 1)]
    [InlineData("using static System.DateTime;\nclass C { void M() { var t = UtcNow; } }", 1)]
    [InlineData("using static System.DateTimeOffset;\nclass C { void M() { var t = Now; } }", 1)]
    [InlineData("using Clock = System.DateTime;\nclass C { void M() { var t = Clock.Now; } }", 1)]
    [InlineData("using Clock = System.DateTimeOffset;\nclass C { void M() { var t = Clock.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = TimeProvider.System.GetUtcNow(); } }", 1)]
    [InlineData("class C { void M() { var t = System.TimeProvider.System; } }", 1)]
    [InlineData("class C { void M(TimeProvider time) { var t = time.GetUtcNow(); } }", 0)]
    [InlineData("class C { void M() { var t = DateTime.MinValue; } }", 0)]
    [InlineData("class C { void M() { var t = other.UtcNow; } }", 0)]
    [InlineData("using Clock = System.TimeSpan;\nclass C { void M() { var t = Clock.Zero; } }", 0)]
    [InlineData("class C { void M() { var t = 1; // DateTime.UtcNow\n } }", 0)]
    public void The_guard_recognises_a_static_clock_read(string source, int expected)
    {
        StaticClockReads.In(source).Should().HaveCount(expected);
    }
}
