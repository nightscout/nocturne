using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class ActiveProfileResolverTests : IDisposable
{
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ActiveProfileResolver _sut;

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // 2024-01-15 12:00:00 UTC in milliseconds
    private const long NoonMills = 1705320000000;

    public ActiveProfileResolverTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);
        _tenantAccessor.Setup(t => t.Context).Returns(new TenantContext(TenantId, "test", "Test", true));

        _sut = new ActiveProfileResolver(
            _stateSpanService.Object,
            _tenantAccessor.Object,
            _cache,
            NullLogger<ActiveProfileResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static StateSpan MakeProfileSpan(
        long startMills,
        long? endMills,
        string? profileName = null,
        double? percentage = null,
        double? timeshift = null)
    {
        var metadata = new Dictionary<string, object>();
        if (profileName is not null)
            metadata["profileName"] = profileName;
        if (percentage.HasValue)
            metadata["percentage"] = percentage.Value;
        if (timeshift.HasValue)
            metadata["timeshift"] = timeshift.Value;

        return new StateSpan
        {
            Id = Guid.NewGuid().ToString(),
            Category = StateSpanCategory.Profile,
            State = "Active",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
            EndTimestamp = endMills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(endMills.Value).UtcDateTime
                : null,
            Metadata = metadata.Count > 0 ? metadata : null,
        };
    }

    private void SetupSpans(params StateSpan[] spans)
    {
        _stateSpanService
            .Setup(s => s.GetStateSpansAsync(
                StateSpanCategory.Profile,
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);
    }

    public class GetActiveProfileNameAsync : ActiveProfileResolverTests
    {
        [Fact]
        public async Task NoProfileSwitches_ReturnsNull()
        {
            SetupSpans();

            var result = await _sut.GetActiveProfileNameAsync(NoonMills);

            result.Should().BeNull();
        }

        [Fact]
        public async Task ActiveProfileSwitch_ReturnsProfileName()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000, // started 1 hour ago
                endMills: null,
                profileName: "Exercise");

            SetupSpans(span);

            var result = await _sut.GetActiveProfileNameAsync(NoonMills);

            result.Should().Be("Exercise");
        }

        [Fact]
        public async Task ExpiredProfileSwitch_ReturnsNull()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 7_200_000, // started 2 hours ago
                endMills: NoonMills - 3_600_000,   // ended 1 hour ago
                profileName: "Exercise");

            SetupSpans(span);

            var result = await _sut.GetActiveProfileNameAsync(NoonMills);

            result.Should().BeNull();
        }

        [Fact]
        public async Task MultipleSpans_ReturnsMostRecentActive()
        {
            var older = MakeProfileSpan(
                startMills: NoonMills - 7_200_000,
                endMills: NoonMills - 3_600_000,
                profileName: "OldProfile");

            var newer = MakeProfileSpan(
                startMills: NoonMills - 1_800_000,
                endMills: null,
                profileName: "CurrentProfile");

            SetupSpans(older, newer);

            var result = await _sut.GetActiveProfileNameAsync(NoonMills);

            result.Should().Be("CurrentProfile");
        }

        [Fact]
        public async Task SpanWithNoMetadata_ReturnsNull()
        {
            var span = new StateSpan
            {
                Id = Guid.NewGuid().ToString(),
                Category = StateSpanCategory.Profile,
                State = "Active",
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills - 3_600_000).UtcDateTime,
                EndTimestamp = null,
                Metadata = null,
            };

            SetupSpans(span);

            var result = await _sut.GetActiveProfileNameAsync(NoonMills);

            result.Should().BeNull();
        }
    }

    public class GetCircadianAdjustmentAsync : ActiveProfileResolverTests
    {
        [Fact]
        public async Task NoProfileSwitches_ReturnsNull()
        {
            SetupSpans();

            var result = await _sut.GetCircadianAdjustmentAsync(NoonMills);

            result.Should().BeNull();
        }

        [Fact]
        public async Task ProfileWithPercentageAndTimeshift_ReturnsAdjustment()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000,
                endMills: null,
                profileName: "Exercise",
                percentage: 85.0,
                timeshift: 1.0);

            SetupSpans(span);

            var result = await _sut.GetCircadianAdjustmentAsync(NoonMills);

            result.Should().NotBeNull();
            result!.Percentage.Should().Be(85.0);
            result.TimeshiftMs.Should().Be(3_600_000);
        }

        [Fact]
        public async Task ProfileWithPercentageOnly_ReturnsAdjustmentWithZeroTimeshift()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000,
                endMills: null,
                profileName: "Exercise",
                percentage: 120.0);

            SetupSpans(span);

            var result = await _sut.GetCircadianAdjustmentAsync(NoonMills);

            result.Should().NotBeNull();
            result!.Percentage.Should().Be(120.0);
            result.TimeshiftMs.Should().Be(0);
        }

        [Fact]
        public async Task ProfileWithoutPercentage_ReturnsNull()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000,
                endMills: null,
                profileName: "Exercise");

            SetupSpans(span);

            var result = await _sut.GetCircadianAdjustmentAsync(NoonMills);

            result.Should().BeNull();
        }
    }

    public class Caching : ActiveProfileResolverTests
    {
        [Fact]
        public async Task SameMinute_DoesNotReQueryStateSpanService()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000,
                endMills: null,
                profileName: "Cached");

            SetupSpans(span);

            // First call populates cache
            await _sut.GetActiveProfileNameAsync(NoonMills);

            // Second call within the same minute should use cache
            await _sut.GetActiveProfileNameAsync(NoonMills + 5_000); // 5 seconds later, same minute

            _stateSpanService.Verify(
                s => s.GetStateSpansAsync(
                    It.IsAny<StateSpanCategory?>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DifferentMinute_RequeriesStateSpanService()
        {
            var span = MakeProfileSpan(
                startMills: NoonMills - 3_600_000,
                endMills: null,
                profileName: "Cached");

            SetupSpans(span);

            await _sut.GetActiveProfileNameAsync(NoonMills);
            await _sut.GetActiveProfileNameAsync(NoonMills + 60_000); // next minute

            _stateSpanService.Verify(
                s => s.GetStateSpansAsync(
                    It.IsAny<StateSpanCategory?>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }
    }
}
