using FluentAssertions;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Connectors.Eversense.Services;
using Xunit;

namespace Nocturne.Connectors.Eversense.Tests.Services;

public class EversenseConnectorServiceTests
{
    [Fact]
    public void SelectPatient_SinglePatient_AutoSelects()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "only@example.com", CurrentGlucose = 100, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().NotBeNull();
        result!.UserName.Should().Be("only@example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_WithConfiguredUsername_SelectsMatch()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "bob@example.com");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("bob@example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_CaseInsensitiveMatch()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "Alice@Example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "alice@example.com");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("Alice@Example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_NoConfiguredUsername_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectPatient_MultiplePatients_UsernameNotFound_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "charlie@example.com");

        result.Should().BeNull();
    }

    [Fact]
    public void SelectPatient_EmptyList_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>();

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().BeNull();
    }
}
