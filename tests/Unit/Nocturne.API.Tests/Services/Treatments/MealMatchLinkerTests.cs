using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Covers what <see cref="MealMatchLinker.LinkAsync"/> hands the repository, and that an unknown
/// food entry or carb intake leaves nothing written.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchLinkerTests
{
    private readonly Mock<IConnectorFoodEntryRepository> _foodEntryRepository = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepository = new();

    private MealMatchLinker NewLinker() =>
        new(_foodEntryRepository.Object, _carbIntakeRepository.Object, Mock.Of<ILogger<MealMatchLinker>>());

    private static ConnectorFoodEntry Entry(Guid id) => new()
    {
        Id = id,
        ConnectorSource = "myfitnesspal-connector",
        MealName = "Breakfast",
        Carbs = 30,
        ConsumedAt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        Status = ConnectorFoodEntryStatus.Pending,
    };

    /// <summary>
    /// <c>treatment_foods.carb_intake_id</c> has no foreign key, so an unknown id would persist
    /// a row nothing can resolve and strand the food entry in Matched.
    /// </summary>
    [Fact]
    public async Task LinkAsync_WritesNothingWhenTheCarbIntakeDoesNotExist()
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdAsync(foodEntryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry(foodEntryId));

        _carbIntakeRepository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CarbIntake?)null);

        var outcome = await NewLinker().LinkAsync(foodEntryId, Guid.CreateVersion7(), 30, 0);

        outcome.Should().Be(AcceptMatchOutcome.NotFound);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task LinkAsync_WritesNothingWhenTheFoodEntryDoesNotExist()
    {
        _foodEntryRepository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorFoodEntry?)null);
        _carbIntakeRepository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = Guid.CreateVersion7(), Carbs = 30 });

        var outcome = await NewLinker().LinkAsync(Guid.NewGuid(), Guid.CreateVersion7(), 30, 0);

        outcome.Should().Be(AcceptMatchOutcome.NotFound);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task LinkAsync_HandsTheRepositoryTheRowToWriteForTheCarbIntake()
    {
        var foodEntryId = Guid.NewGuid();
        var carbIntakeId = Guid.CreateVersion7();
        _foodEntryRepository
            .Setup(r => r.GetByIdAsync(foodEntryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry(foodEntryId));
        _carbIntakeRepository
            .Setup(r => r.GetByIdAsync(carbIntakeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = carbIntakeId, Carbs = 30 });
        _foodEntryRepository
            .Setup(r => r.AcceptMatchAsync(foodEntryId, It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptMatchOutcome.Linked);

        var outcome = await NewLinker().LinkAsync(foodEntryId, carbIntakeId, 30, 15);

        outcome.Should().Be(AcceptMatchOutcome.Linked);
        _foodEntryRepository.Verify(
            r => r.AcceptMatchAsync(
                foodEntryId,
                It.Is<TreatmentFood>(f => f.CarbIntakeId == carbIntakeId && f.Carbs == 30 && f.TimeOffsetMinutes == 15),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(AcceptMatchOutcome.AlreadyLinked)]
    [InlineData(AcceptMatchOutcome.AlreadyResolved)]
    [InlineData(AcceptMatchOutcome.NotFound)]
    public async Task LinkAsync_ReportsWhateverTheRepositoryDecidedAboutTheClaim(AcceptMatchOutcome decided)
    {
        var foodEntryId = Guid.NewGuid();
        var carbIntakeId = Guid.CreateVersion7();
        _foodEntryRepository
            .Setup(r => r.GetByIdAsync(foodEntryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry(foodEntryId));
        _carbIntakeRepository
            .Setup(r => r.GetByIdAsync(carbIntakeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = carbIntakeId, Carbs = 30 });
        _foodEntryRepository
            .Setup(r => r.AcceptMatchAsync(foodEntryId, It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decided);

        var outcome = await NewLinker().LinkAsync(foodEntryId, carbIntakeId, 30, 0);

        outcome.Should().Be(decided);
    }

    private void VerifyNothingWritten() =>
        _foodEntryRepository.Verify(
            r => r.AcceptMatchAsync(It.IsAny<Guid>(), It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Never);
}
