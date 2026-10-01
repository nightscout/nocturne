using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// <c>POST</c>/<c>PUT /api/v4/profile/settings</c> never write <see cref="TherapySettings.IsDefault"/>
/// directly: <c>true</c> goes through <see cref="ITherapySettingsRepository.SetDefaultAsync"/>, and
/// <c>false</c> never demotes the current default.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileControllerSettingsDefaultTests
{
    private readonly List<TherapySettings> _rows = [];
    private readonly ProfileController _controller;

    public ProfileControllerSettingsDefaultTests()
    {
        var repo = new Mock<ITherapySettingsRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Clone(_rows.FirstOrDefault(r => r.Id == id)));
        repo.Setup(r => r.CreateAsync(It.IsAny<TherapySettings>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TherapySettings m, WriteOrigin _, CancellationToken _) =>
            {
                var row = Clone(m)!;
                row.Id = Guid.NewGuid();
                _rows.Add(row);
                return Clone(row)!;
            });
        repo.Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TherapySettings>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, TherapySettings m, WriteOrigin _, CancellationToken _) =>
            {
                var index = _rows.FindIndex(r => r.Id == id);
                if (index < 0)
                    throw new KeyNotFoundException();
                var row = Clone(m)!;
                row.Id = id;
                row.IsDefault = _rows[index].IsDefault;
                _rows[index] = row;
                return Clone(row)!;
            });
        repo.Setup(r => r.SetDefaultAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid? id, CancellationToken _) =>
            {
                foreach (var row in _rows)
                    row.IsDefault = row.Id == id;
                return Task.CompletedTask;
            });

        _controller = new ProfileController(
            repo.Object,
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>()
        );
    }

    private static TherapySettings? Clone(TherapySettings? source) =>
        source is null
            ? null
            : new TherapySettings
            {
                Id = source.Id,
                Timestamp = source.Timestamp,
                ProfileName = source.ProfileName,
                IsDefault = source.IsDefault,
            };

    private TherapySettings Seed(string name, bool isDefault)
    {
        var row = new TherapySettings
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            ProfileName = name,
            IsDefault = isDefault,
        };
        _rows.Add(row);
        return row;
    }

    private static TherapySettings Request(string name, bool isDefault) =>
        new() { Timestamp = DateTime.UtcNow, ProfileName = name, IsDefault = isDefault };

    [Fact]
    public async Task Create_WithIsDefaultTrue_LeavesTheNewRowAsTheOnlyDefault()
    {
        Seed("Old", isDefault: true);

        var result = await _controller.CreateTherapySettings(Request("New", isDefault: true));

        var created = (TherapySettings)result.Result.As<CreatedAtActionResult>().Value!;
        created.IsDefault.Should().BeTrue();
        _rows.Where(r => r.IsDefault).Should().ContainSingle().Which.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Create_WithIsDefaultFalse_KeepsTheExistingDefault()
    {
        var old = Seed("Old", isDefault: true);

        await _controller.CreateTherapySettings(Request("New", isDefault: false));

        _rows.Where(r => r.IsDefault).Should().ContainSingle().Which.Id.Should().Be(old.Id);
    }

    [Fact]
    public async Task Update_WithIsDefaultTrue_LeavesThatRowAsTheOnlyDefault()
    {
        Seed("Old", isDefault: true);
        var target = Seed("Target", isDefault: false);

        var result = await _controller.UpdateTherapySettings(target.Id, Request("Target", isDefault: true));

        ((TherapySettings)result.Result.As<OkObjectResult>().Value!).IsDefault.Should().BeTrue();
        _rows.Where(r => r.IsDefault).Should().ContainSingle().Which.Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task Update_OfAnotherRowWithIsDefaultFalse_KeepsTheExistingDefault()
    {
        var old = Seed("Old", isDefault: true);
        var other = Seed("Other", isDefault: false);

        await _controller.UpdateTherapySettings(other.Id, Request("Other", isDefault: false));

        _rows.Where(r => r.IsDefault).Should().ContainSingle().Which.Id.Should().Be(old.Id);
    }

    [Fact]
    public async Task Update_OfTheDefaultRowWithIsDefaultFalse_DoesNotDemoteIt()
    {
        var old = Seed("Old", isDefault: true);

        var result = await _controller.UpdateTherapySettings(old.Id, Request("Old", isDefault: false));

        ((TherapySettings)result.Result.As<OkObjectResult>().Value!).IsDefault.Should().BeTrue();
        _rows.Where(r => r.IsDefault).Should().ContainSingle().Which.Id.Should().Be(old.Id);
    }

    [Fact]
    public async Task Update_OfAMissingRow_ReturnsNotFound()
    {
        var result = await _controller.UpdateTherapySettings(Guid.NewGuid(), Request("X", isDefault: true));

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
