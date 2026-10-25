using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class MetadataControllerSyncPhasesTests
{
    [Fact]
    public void GetSyncPhases_OffersEveryPhaseAsItsWireName()
    {
        var metadata = new MetadataController().GetSyncPhases()
            .Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<SyncPhasesMetadata>().Subject;

        metadata.AvailablePhases
            .Select(phase => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(phase)))
            .Should().Equal("Syncing", "Completed", "Failed");
        metadata.Description.Should().NotBeNullOrWhiteSpace();
    }
}
