using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class LegacyTreatmentTablesTests
{
    [Fact]
    public void ByServedPrecedence_HoldsEveryKeyedTableOnce()
    {
        var keyed = LegacyTreatmentTables.All.OfType<ILegacyKeyedTreatmentTable>().ToList();

        LegacyTreatmentTables.ByServedPrecedence.Should().HaveCount(keyed.Count);
        LegacyTreatmentTables.ByServedPrecedence.ToHashSet().SetEquals(keyed).Should().BeTrue();
    }

    [Fact]
    public void All_OnlyStateSpanTablesAreUnkeyed()
    {
        LegacyTreatmentTables.All.Where(table => table is not ILegacyKeyedTreatmentTable).ToHashSet()
            .SetEquals(LegacyTreatmentTables.StateSpanTables).Should().BeTrue();
    }

    [Theory]
    [InlineData(StateSpanCategory.Override, "t1", true)]
    [InlineData(StateSpanCategory.TemporaryTarget, "t1", true)]
    [InlineData(StateSpanCategory.Override, null, false)]
    [InlineData(StateSpanCategory.PumpMode, "t1", false)]
    public void ServesStateSpan_IsTheServedCategoriesOfSpansWrittenFromATreatment(
        StateSpanCategory category, string? originalId, bool served)
    {
        LegacyTreatmentTables.ServesStateSpan(new StateSpan { Category = category, OriginalId = originalId })
            .Should().Be(served);
    }

    [Fact]
    public void AProfileSwitchIsServedWithItsDataSource()
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow, LegacyId = "switch-1", DataSource = "nightscout-connector",
        };

        LegacyTreatmentTables.ProjectProfileActivation(activation).DataSource.Should().Be("nightscout-connector");
    }

    [Fact]
    public void EveryTableReadsItsInstantsAsOneIndexableAnyNotAWindowOverItsWholeTable()
    {
        using var context = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql("Host=unused").Options)
        {
            TenantId = Guid.NewGuid(),
        };
        var instants = new[] { DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1) };

        foreach (var table in LegacyTreatmentTables.All)
        {
            var sql = table.AtQuery(context, instants).ToQueryString();

            sql.Should().Contain("= ANY (", table.RecordType);
            sql.Should().NotContain("ROW_NUMBER", table.RecordType);
            sql.Should().NotContain("LATERAL", table.RecordType);
        }
    }
}
