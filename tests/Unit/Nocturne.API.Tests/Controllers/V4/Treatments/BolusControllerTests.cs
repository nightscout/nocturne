using FluentAssertions;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Treatments;

public class BolusControllerTests
{
    private sealed class Exposed()
        : BolusController(
            Mock.Of<IBolusRepository>(),
            Mock.Of<IPatientInsulinRepository>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>())
    {
        public Bolus Map(CreateBolusRequest request) => MapCreateToModel(request);
    }

    [Theory]
    [InlineData(true, BolusKind.Manual, BolusKind.Algorithm)]
    [InlineData(true, BolusKind.Algorithm, BolusKind.Algorithm)]
    [InlineData(false, BolusKind.Manual, BolusKind.Manual)]
    [InlineData(false, BolusKind.Algorithm, BolusKind.Algorithm)]
    public void Create_StoresAnAutomaticBolusAsAlgorithm(bool automatic, BolusKind requested, BolusKind stored)
    {
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 0.4,
            Automatic = automatic,
            Kind = requested,
        };

        var bolus = new Exposed().Map(request);

        bolus.Kind.Should().Be(stored);
        bolus.Automatic.Should().Be(automatic);
    }
}
