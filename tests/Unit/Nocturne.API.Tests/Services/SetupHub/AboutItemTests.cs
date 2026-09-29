using FluentAssertions;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Services.SetupHub.Items;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

public class AboutItemTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly NocturneDbContext _db;
    private readonly SetupHubService _service;

    public AboutItemTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext($"setup_hub_about_{Guid.NewGuid()}");
        _db.TenantId = TenantId;
        _db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "own", DisplayName = "Own" });
        _db.SaveChanges();
        _service = new SetupHubService(_db, [new AboutItem(_db)]);
    }

    private async Task<SetupHubItemState> StateAsync() =>
        (await _service.GetAsync(CancellationToken.None)).Items.Single(i => i.Key == SetupHubItemKey.About).State;

    [Fact]
    public async Task IsOpen_WhileTheRecordHoldsOnlyWhatTheCoreWrote()
    {
        _db.PatientRecords.Add(new PatientRecordEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, PreferredName = "Sam", Timezone = "Pacific/Auckland",
        });
        await _db.SaveChangesAsync();

        (await StateAsync()).Should().Be(SetupHubItemState.Open);
    }

    [Theory]
    [InlineData(nameof(PatientRecordEntity.DiabetesType))]
    [InlineData(nameof(PatientRecordEntity.DateOfBirth))]
    [InlineData(nameof(PatientRecordEntity.DiagnosisDate))]
    [InlineData(nameof(PatientRecordEntity.Sex))]
    [InlineData(nameof(PatientRecordEntity.Pronouns))]
    public async Task IsDone_OnceAnyClinicalFieldIsSaved(string field)
    {
        var record = new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = TenantId };
        _db.PatientRecords.Add(record);
        await _db.SaveChangesAsync();
        (await StateAsync()).Should().Be(SetupHubItemState.Open);

        switch (field)
        {
            case nameof(PatientRecordEntity.DiabetesType): record.DiabetesType = "Type1"; break;
            case nameof(PatientRecordEntity.DateOfBirth): record.DateOfBirth = new DateOnly(1990, 5, 1); break;
            case nameof(PatientRecordEntity.DiagnosisDate): record.DiagnosisDate = new DateOnly(2020, 1, 1); break;
            case nameof(PatientRecordEntity.Sex): record.Sex = "Female"; break;
            default: record.Pronouns = "they/them"; break;
        }
        await _db.SaveChangesAsync();

        (await StateAsync()).Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task IsDone_EvenAfterBeingSetAside()
    {
        await _service.SetStateAsync(SetupHubItemKey.About, SetupHubItemState.NotForMe, CancellationToken.None);
        _db.PatientRecords.Add(new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, DiabetesType = "Type2" });
        await _db.SaveChangesAsync();

        (await StateAsync()).Should().Be(SetupHubItemState.Done);
    }
}
