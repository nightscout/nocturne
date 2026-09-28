using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// How <c>set-default</c> resolves a caller-supplied profile name. Names are stored
/// case-sensitively, so a lenient match has to stop short of guessing between two profiles that
/// differ only by case.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileControllerNameResolutionTests
{
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IProfileDeletionService> _deletionService = new();
    private readonly ProfileController _sut;

    public ProfileControllerNameResolutionTests()
    {
        _sut = new ProfileController(
            _therapyRepo.Object,
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>(),
            _deletionService.Object);
    }

    private void Stored(params TherapySettings[] settings) =>
        _therapyRepo
            .Setup(r => r.GetAsync(null, null, null, null, 1000, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

    private static TherapySettings Profile(string name, bool isDefault = false, int minutesOld = 0) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = name,
        IsDefault = isDefault,
        Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesOld),
    };

    [Fact]
    public async Task SetDefault_PrefersAnExactMatch_OverADifferentlyCasedOne()
    {
        var lower = Profile("default", minutesOld: 10);
        var upper = Profile("Default", isDefault: true, minutesOld: 0);
        Stored(upper, lower);

        var result = await _sut.SetDefaultProfile("default");

        result.Should().BeOfType<NoContentResult>();
        _therapyRepo.Verify(
            r => r.SetDefaultAsync(lower.Id, It.IsAny<CancellationToken>()),
            Times.Once,
            "the exactly-named profile is the one activated, not the newer differently-cased row");
    }

    [Fact]
    public async Task SetDefault_AcceptsADifferentCase_WhenOnlyOneProfileCouldBeMeant()
    {
        var stored = Profile("Default");
        Stored(stored);

        var result = await _sut.SetDefaultProfile("DEFAULT");

        result.Should().BeOfType<NoContentResult>();
        _therapyRepo.Verify(
            r => r.SetDefaultAsync(stored.Id, It.IsAny<CancellationToken>()),
            Times.Once,
            "a lone candidate is unambiguous, so the lenient calls this endpoint has always served still work");
    }

    /// <summary>
    /// The pair that motivated this: <c>Default</c> relayed from an uploader beside the
    /// <c>default</c> a pump writes. Neither is the obvious intent, so neither is chosen.
    /// </summary>
    [Fact]
    public async Task SetDefault_RefusesToGuess_WhenTwoProfilesDifferOnlyByCase()
    {
        Stored(Profile("Default"), Profile("default", minutesOld: 10));

        var result = await _sut.SetDefaultProfile("DEFAULT");

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Contain("Default").And.Contain("default");

        _therapyRepo.Verify(
            r => r.SetDefaultAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetDefault_ReportsAnUnknownNameAsNotFound()
    {
        Stored(Profile("Default"));

        var result = await _sut.SetDefaultProfile("Nonexistent");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(ProfileDeletionRefusal.ActiveProfile)]
    [InlineData(ProfileDeletionRefusal.OnlyProfile)]
    public async Task Delete_TranslatesARefusalIntoAConflictNamingTheProfile(ProfileDeletionRefusal refusal)
    {
        _deletionService
            .Setup(s => s.DeleteByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDeletionResult(refusal, 0));

        var result = await _sut.DeleteProfileByName("Default");

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Contain("Default");
    }

    [Fact]
    public async Task Delete_ReportsAnUnknownNameAsNotFound()
    {
        _deletionService
            .Setup(s => s.DeleteByProfileNameAsync("Nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDeletionResult(ProfileDeletionRefusal.NotFound, 0));

        var result = await _sut.DeleteProfileByName("Nope");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_PassesTheNameThroughUntouched()
    {
        _deletionService
            .Setup(s => s.DeleteByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDeletionResult(ProfileDeletionRefusal.None, 5));

        var result = await _sut.DeleteProfileByName("Default");

        result.Should().BeOfType<NoContentResult>();
        _deletionService.Verify(
            s => s.DeleteByProfileNameAsync("Default", It.IsAny<CancellationToken>()), Times.Once,
            "the delete never falls back to a case-insensitive match");
    }
}
