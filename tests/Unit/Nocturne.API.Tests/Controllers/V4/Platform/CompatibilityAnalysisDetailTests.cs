using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Services.Compatibility;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The analysis-detail endpoints serve any analysis in the tenant's history, not just the newest
/// one, and carry the same child discrepancies the list endpoint loads. Driven over the real
/// repository so the id predicate is proven in the query rather than in memory.
/// </summary>
[Trait("Category", "Unit")]
public class CompatibilityAnalysisDetailTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private static readonly Guid OlderAnalysisId = new("01900000-0000-7000-8000-000000000001");
    private static readonly Guid NewerAnalysisId = new("01900000-0000-7000-8000-000000000002");
    private static readonly Guid OtherTenantAnalysisId = new("01900000-0000-7000-8000-000000000003");

    public CompatibilityAnalysisDetailTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions) { TenantId = _tenantId };
        seed.Database.EnsureCreated();
        seed.Tenants.Add(Tenant(_tenantId, "default"));
        seed.DiscrepancyAnalyses.Add(
            Analysis(OlderAnalysisId, "trace-older", DateTimeOffset.UnixEpoch));
        seed.DiscrepancyAnalyses.Add(
            Analysis(NewerAnalysisId, "trace-newer", DateTimeOffset.UnixEpoch.AddDays(1)));
        seed.DiscrepancyDetails.Add(Detail(OlderAnalysisId, "older-field"));
        seed.DiscrepancyDetails.Add(Detail(NewerAnalysisId, "newer-field"));
        seed.SaveChanges();

        using var otherSeed = new NocturneDbContext(_dbOptions) { TenantId = _otherTenantId };
        otherSeed.Tenants.Add(Tenant(_otherTenantId, "other"));
        otherSeed.DiscrepancyAnalyses.Add(
            Analysis(OtherTenantAnalysisId, "trace-other", DateTimeOffset.UnixEpoch.AddDays(2)));
        otherSeed.SaveChanges();
    }

    [Fact]
    public async Task GetAnalysisDetail_ServesAnAnalysisOlderThanTheNewest()
    {
        var detail = await DetailAsync(OlderAnalysisId);

        detail.Id.Should().Be(OlderAnalysisId);
        detail.TraceId.Should().Be("trace-older");
    }

    [Fact]
    public async Task GetAnalysisDetail_LoadsTheOlderAnalysisChildDiscrepancies()
    {
        var detail = await DetailAsync(OlderAnalysisId);

        detail.Discrepancies.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Field = "older-field",
                DiscrepancyType = DiscrepancyType.StringValue,
                Severity = DiscrepancySeverity.Minor,
                NightscoutValue = "1",
                NocturneValue = "2",
                Description = "differs",
            });
    }

    [Fact]
    public async Task GetAnalysisDetail_StillServesTheNewestAnalysis()
    {
        var detail = await DetailAsync(NewerAnalysisId);

        detail.Id.Should().Be(NewerAnalysisId);
        detail.Discrepancies.Should().ContainSingle().Which.Field.Should().Be("newer-field");
    }

    [Fact]
    public async Task GetAnalysisDetail_DoesNotServeAnotherTenantsAnalysis()
    {
        var result = await CreateCompatibilityController()
            .GetAnalysisDetail(OtherTenantAnalysisId);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAnalysisDetail_IsNotFoundForAnUnknownId()
    {
        var result = await CreateCompatibilityController().GetAnalysisDetail(Guid.CreateVersion7());

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetDiscrepancyAnalysis_ServesAnAnalysisOlderThanTheNewest()
    {
        var controller = new DiscrepancyController(
            CreateRepository(), Mock.Of<ILogger<DiscrepancyController>>());

        var result = await controller.GetDiscrepancyAnalysis(OlderAnalysisId);

        var analysis = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<DiscrepancyAnalysisDto>().Subject;
        analysis.Id.Should().Be(OlderAnalysisId);
        analysis.Discrepancies.Should().ContainSingle().Which.Field.Should().Be("older-field");
    }

    [Fact]
    public async Task GetDiscrepancyAnalysis_DoesNotServeAnotherTenantsAnalysis()
    {
        var controller = new DiscrepancyController(
            CreateRepository(), Mock.Of<ILogger<DiscrepancyController>>());

        var result = await controller.GetDiscrepancyAnalysis(OtherTenantAnalysisId);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    private async Task<AnalysisDetailDto> DetailAsync(Guid id)
    {
        var result = await CreateCompatibilityController().GetAnalysisDetail(id);

        return result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AnalysisDetailDto>().Subject;
    }

    private CompatibilityController CreateCompatibilityController() =>
        new(
            Mock.Of<IDiscrepancyPersistenceService>(),
            CreateRepository(),
            Options.Create(new CompatibilityProxyConfiguration()),
            Mock.Of<ILogger<CompatibilityController>>());

    private DiscrepancyAnalysisRepository CreateRepository() =>
        new(new NocturneDbContext(_dbOptions) { TenantId = _tenantId });

    private static TenantEntity Tenant(Guid id, string slug) =>
        new()
        {
            Id = id,
            Slug = slug,
            DisplayName = slug,
            IsActive = true,
        };

    private static DiscrepancyAnalysisEntity Analysis(
        Guid id, string traceId, DateTimeOffset timestamp) =>
        new()
        {
            Id = id,
            TraceId = traceId,
            AnalysisTimestamp = timestamp,
            RequestMethod = "GET",
            RequestPath = "/api/v1/entries",
            OverallMatch = ResponseMatchType.MinorDifferences,
            StatusCodeMatch = true,
            BodyMatch = false,
            TotalProcessingTimeMs = 12,
            Summary = "one field differs",
            MinorDiscrepancyCount = 1,
        };

    private static DiscrepancyDetailEntity Detail(Guid analysisId, string field) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AnalysisId = analysisId,
            DiscrepancyType = DiscrepancyType.StringValue,
            Severity = DiscrepancySeverity.Minor,
            Field = field,
            NightscoutValue = "1",
            NocturneValue = "2",
            Description = "differs",
            RecordedAt = DateTimeOffset.UnixEpoch,
        };

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
