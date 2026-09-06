using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nocturne.API.Controllers.V4.TenantAdmin;

namespace Nocturne.API.Tests.Controllers.V4.TenantAdmin;

[Trait("Category", "Unit")]
public class DeduplicationControllerTests
{
    private readonly Mock<IDeduplicationService> _deduplicationService = new();

    private DeduplicationController BuildController() => new(
        _deduplicationService.Object,
        new Mock<ILogger<DeduplicationController>>().Object);

    [Fact]
    public async Task GetRecordLinkedRecords_ReturnsTheWholeGroupForANonPrimaryMember()
    {
        var canonicalId = Guid.CreateVersion7();
        var primaryId = Guid.CreateVersion7();
        var nonPrimaryId = Guid.CreateVersion7();

        _deduplicationService
            .Setup(s => s.GetLinkedRecordAsync(RecordType.Bolus, nonPrimaryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LinkedRecord
            {
                CanonicalId = canonicalId,
                // Deliberately not the requested type, so the assertion below pins the response
                // to the route value rather than to whatever the link row happens to carry.
                RecordType = RecordType.Note,
                RecordId = nonPrimaryId,
                IsPrimary = false
            });
        _deduplicationService
            .Setup(s => s.GetLinkedRecordsAsync(canonicalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new LinkedRecord { CanonicalId = canonicalId, RecordId = primaryId, IsPrimary = true },
                new LinkedRecord { CanonicalId = canonicalId, RecordId = nonPrimaryId, IsPrimary = false }
            ]);

        var result = await BuildController()
            .GetRecordLinkedRecords(RecordType.Bolus, nonPrimaryId.ToString());

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LinkedRecordsResponse>().Subject;
        response.CanonicalId.Should().Be(canonicalId);
        response.RecordType.Should().Be(RecordType.Bolus);
        response.LinkedRecords.Select(r => r.RecordId).Should().Equal(primaryId, nonPrimaryId);
    }

    [Fact]
    public async Task GetRecordLinkedRecords_Returns404ForAnUnlinkedRecord()
    {
        _deduplicationService
            .Setup(s => s.GetLinkedRecordAsync(
                It.IsAny<RecordType>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LinkedRecord?)null);

        var result = await BuildController()
            .GetRecordLinkedRecords(RecordType.Bolus, Guid.CreateVersion7().ToString());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetRecordLinkedRecords_Returns400ForAnIdThatIsNotAGuid()
    {
        var result = await BuildController()
            .GetRecordLinkedRecords(RecordType.Bolus, "not-a-guid");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
        _deduplicationService.Verify(
            s => s.GetLinkedRecordAsync(
                It.IsAny<RecordType>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
