using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Services.Health.Oura;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Health.Oura;

[Trait("Category", "Unit")]
public class OuraControllerTests
{
    private readonly OuraImportHarness _h = new();
    private readonly OuraImportJobService _jobs;
    private readonly IServiceProvider _request;

    public OuraControllerTests()
    {
        (_jobs, var root) = _h.Build();
        _request = root.CreateScope().ServiceProvider;
        _request.GetRequiredService<ITenantAccessor>().SetTenant(OuraImportHarness.Tenant);
    }

    private OuraController Controller() => new(_jobs, _request.GetRequiredService<ITenantAccessor>())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _request } },
        ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
    };

    private static IFormFile File(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "oura.zip");

    private static Mock<IFormFile> UnreadFile()
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(100);
        return file;
    }

    private static void ShouldBeProblem(ActionResult<OuraImportStatus> result, int status, string code)
    {
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(code);
    }

    [Fact]
    public async Task AValidExport_StartsTheImport_AndAnswersAccepted()
    {
        var result = await Controller().ImportOuraExport(File(OuraImportHarness.Export()), CancellationToken.None);

        var accepted = result.Result.Should().BeOfType<AcceptedAtActionResult>().Subject;
        accepted.Value.Should().BeOfType<OuraImportStatus>();
        _jobs.GetLatest(OuraImportHarness.Tenant.TenantId).Should().NotBeNull();
        await OuraImportHarness.EndOf(_jobs);
    }

    [Fact]
    public async Task AFileThatIsNotAnExport_IsRefused_AndStartsNoImport()
    {
        var result = await Controller().ImportOuraExport(File("hello"u8.ToArray()), CancellationToken.None);

        ShouldBeProblem(result, StatusCodes.Status400BadRequest, OuraExportException.NotAZip);
        _jobs.GetLatest(OuraImportHarness.Tenant.TenantId).Should().BeNull();
    }

    [Fact]
    public async Task AnExportPastABudget_IsRefused_AndStartsNoImport()
    {
        var result = await Controller().ImportOuraExport(
            File(OuraImportServiceTests.OverStageBudget().ToArray()), CancellationToken.None);

        ShouldBeProblem(result, StatusCodes.Status400BadRequest, OuraExportException.TooMuchData);
        _jobs.GetLatest(OuraImportHarness.Tenant.TenantId).Should().BeNull();
        _h.WrittenUnder.Should().BeEmpty();
    }

    [Fact]
    public async Task WhileAnImportRuns_ASecondUploadIsRefusedUnread()
    {
        _h.StoreSession = _ => _h.SleepGate.Task;
        await Controller().ImportOuraExport(File(OuraImportHarness.Export()), CancellationToken.None);
        var second = UnreadFile();

        var result = await Controller().ImportOuraExport(second.Object, CancellationToken.None);

        ShouldBeProblem(result, StatusCodes.Status409Conflict, "import_running");
        second.Verify(f => f.OpenReadStream(), Times.Never);
        _h.SleepGate.SetResult();
        await OuraImportHarness.EndOf(_jobs);
    }

    [Fact]
    public async Task WhileADeleteRuns_AnUploadIsRefusedAsADelete()
    {
        using var delete = _h.RunGuard.TryAcquire(OuraImportHarness.Tenant.TenantId, OuraImportJobService.RunName);

        var result = await Controller().ImportOuraExport(File(OuraImportHarness.Export()), CancellationToken.None);

        ShouldBeProblem(result, StatusCodes.Status409Conflict, "delete_running");
        _jobs.GetLatest(OuraImportHarness.Tenant.TenantId).Should().BeNull();
    }

    [Fact]
    public async Task AHistoryClampedCaller_IsRefusedBeforeTheUploadIsRead()
    {
        _request.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        var file = UnreadFile();

        var result = await Controller().ImportOuraExport(file.Object, CancellationToken.None);

        ShouldBeProblem(result, StatusCodes.Status403Forbidden, "history_clamped");
        file.Verify(f => f.OpenReadStream(), Times.Never);
        _jobs.GetLatest(OuraImportHarness.Tenant.TenantId).Should().BeNull();
    }

    [Fact]
    public void TheStatusQuery_AnswersNotFoundWhenThereIsNoImport()
    {
        Controller().GetOuraImport().Result.Should().BeOfType<NotFoundResult>();
    }
}
