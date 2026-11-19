using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class TransmitterStatusControllerTests
{
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshots = new();

    private TransmitterStatusController Controller() => new(_uploaderSnapshots.Object);

    [Fact]
    public async Task ReturnsTheLatestTransmitterStatus()
    {
        var uploaded = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        _uploaderSnapshots
            .Setup(r => r.GetLatestOfTypeAsync(TransmitterStatus.UploaderType, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploaderSnapshot
            {
                Timestamp = uploaded,
                Device = "G6 Transmitter",
                Type = TransmitterStatus.UploaderType,
                Temperature = 30,
                AdditionalProperties = new()
                {
                    ["voltagea"] = JsonSerializer.SerializeToElement(288),
                    ["voltageb"] = JsonSerializer.SerializeToElement(270),
                    ["resistance"] = JsonSerializer.SerializeToElement(1450),
                    ["days"] = JsonSerializer.SerializeToElement(97),
                    ["status"] = JsonSerializer.SerializeToElement("LOW"),
                },
            });

        var result = await Controller().GetTransmitterStatus();

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new TransmitterStatus
        {
            Timestamp = uploaded,
            Device = "G6 Transmitter",
            VoltageA = 288,
            VoltageB = 270,
            Resistance = 1450,
            Days = 97,
            Status = "LOW",
            Temperature = 30,
        });
    }

    [Fact]
    public async Task ReturnsNoContentWhenNoUploaderHasReportedATransmitter()
    {
        var result = await Controller().GetTransmitterStatus();

        result.Result.Should().BeOfType<NoContentResult>();
    }
}
