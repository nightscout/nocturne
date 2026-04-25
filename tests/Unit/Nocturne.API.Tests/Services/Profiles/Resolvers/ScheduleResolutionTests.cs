using FluentAssertions;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class ScheduleResolutionTests
{
    private const int Midnight = 0;
    private const int ThreeAm = 3 * 3600;
    private const int SixAm = 6 * 3600;
    private const int Noon = 12 * 3600;
    private const int TenPm = 22 * 3600;
    private const int ElevenPm = 23 * 3600;

    public class FindValueAtTime : ScheduleResolutionTests
    {
        [Fact]
        public void EmptyEntries_ReturnsNull()
        {
            var result = ScheduleResolution.FindValueAtTime([], Noon);

            result.Should().BeNull();
        }

        [Fact]
        public void SingleEntryAtMidnight_ReturnsValueForAnyTime()
        {
            var entries = new List<ScheduleEntry>
            {
                new() { Time = "00:00", Value = 1.0, TimeAsSeconds = Midnight },
            };

            ScheduleResolution.FindValueAtTime(entries, ThreeAm).Should().Be(1.0);
            ScheduleResolution.FindValueAtTime(entries, Noon).Should().Be(1.0);
            ScheduleResolution.FindValueAtTime(entries, ElevenPm).Should().Be(1.0);
        }

        [Theory]
        [InlineData(ThreeAm, 1.0)]
        [InlineData(Noon, 0.8)]
        [InlineData(ElevenPm, 1.2)]
        public void MultipleEntries_ReturnsCorrectValueForTime(int secondsFromMidnight, double expected)
        {
            var entries = new List<ScheduleEntry>
            {
                new() { Time = "00:00", Value = 1.0, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Value = 0.8, TimeAsSeconds = SixAm },
                new() { Time = "22:00", Value = 1.2, TimeAsSeconds = TenPm },
            };

            var result = ScheduleResolution.FindValueAtTime(entries, secondsFromMidnight);

            result.Should().Be(expected);
        }

        [Fact]
        public void TimeExactlyOnBoundary_ReturnsThatBoundaryValue()
        {
            var entries = new List<ScheduleEntry>
            {
                new() { Time = "00:00", Value = 1.0, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Value = 0.8, TimeAsSeconds = SixAm },
                new() { Time = "22:00", Value = 1.2, TimeAsSeconds = TenPm },
            };

            ScheduleResolution.FindValueAtTime(entries, SixAm).Should().Be(0.8);
            ScheduleResolution.FindValueAtTime(entries, TenPm).Should().Be(1.2);
            ScheduleResolution.FindValueAtTime(entries, Midnight).Should().Be(1.0);
        }

        [Fact]
        public void EntriesNotPreSorted_StillWorksCorrectly()
        {
            var entries = new List<ScheduleEntry>
            {
                new() { Time = "22:00", Value = 1.2, TimeAsSeconds = TenPm },
                new() { Time = "00:00", Value = 1.0, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Value = 0.8, TimeAsSeconds = SixAm },
            };

            ScheduleResolution.FindValueAtTime(entries, ThreeAm).Should().Be(1.0);
            ScheduleResolution.FindValueAtTime(entries, Noon).Should().Be(0.8);
            ScheduleResolution.FindValueAtTime(entries, ElevenPm).Should().Be(1.2);
        }

        [Fact]
        public void NullTimeAsSeconds_TreatedAsZero()
        {
            var entries = new List<ScheduleEntry>
            {
                new() { Time = "00:00", Value = 1.0, TimeAsSeconds = null },
                new() { Time = "06:00", Value = 0.8, TimeAsSeconds = SixAm },
            };

            ScheduleResolution.FindValueAtTime(entries, ThreeAm).Should().Be(1.0);
            ScheduleResolution.FindValueAtTime(entries, Noon).Should().Be(0.8);
        }
    }

    public class FindRangeAtTime : ScheduleResolutionTests
    {
        [Fact]
        public void EmptyEntries_ReturnsNull()
        {
            var result = ScheduleResolution.FindRangeAtTime([], Noon);

            result.Should().BeNull();
        }

        [Fact]
        public void SingleEntryAtMidnight_ReturnsRangeForAnyTime()
        {
            var entries = new List<TargetRangeEntry>
            {
                new() { Time = "00:00", Low = 70, High = 180, TimeAsSeconds = Midnight },
            };

            var result = ScheduleResolution.FindRangeAtTime(entries, Noon);

            result.Should().Be((70.0, 180.0));
        }

        [Theory]
        [InlineData(ThreeAm, 70, 180)]
        [InlineData(Noon, 80, 140)]
        [InlineData(ElevenPm, 90, 160)]
        public void MultipleEntries_ReturnsCorrectRangeForTime(
            int secondsFromMidnight,
            double expectedLow,
            double expectedHigh
        )
        {
            var entries = new List<TargetRangeEntry>
            {
                new() { Time = "00:00", Low = 70, High = 180, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Low = 80, High = 140, TimeAsSeconds = SixAm },
                new() { Time = "22:00", Low = 90, High = 160, TimeAsSeconds = TenPm },
            };

            var result = ScheduleResolution.FindRangeAtTime(entries, secondsFromMidnight);

            result.Should().Be(((double)expectedLow, (double)expectedHigh));
        }

        [Fact]
        public void TimeExactlyOnBoundary_ReturnsThatBoundaryRange()
        {
            var entries = new List<TargetRangeEntry>
            {
                new() { Time = "00:00", Low = 70, High = 180, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Low = 80, High = 140, TimeAsSeconds = SixAm },
            };

            var result = ScheduleResolution.FindRangeAtTime(entries, SixAm);

            result.Should().Be((80.0, 140.0));
        }

        [Fact]
        public void EntriesNotPreSorted_StillWorksCorrectly()
        {
            var entries = new List<TargetRangeEntry>
            {
                new() { Time = "22:00", Low = 90, High = 160, TimeAsSeconds = TenPm },
                new() { Time = "00:00", Low = 70, High = 180, TimeAsSeconds = Midnight },
                new() { Time = "06:00", Low = 80, High = 140, TimeAsSeconds = SixAm },
            };

            ScheduleResolution.FindRangeAtTime(entries, ThreeAm).Should().Be((70.0, 180.0));
            ScheduleResolution.FindRangeAtTime(entries, Noon).Should().Be((80.0, 140.0));
            ScheduleResolution.FindRangeAtTime(entries, ElevenPm).Should().Be((90.0, 160.0));
        }
    }
}
