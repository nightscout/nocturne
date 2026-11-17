using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.McpServer.Commands;
using Nocturne.Tools.McpServer.Configuration;
using Xunit;

namespace Nocturne.Tools.McpServer.Tests;

public class VersionCommandTests
{
    [Fact]
    public void It_takes_the_clock_it_is_given()
    {
        var command = () => new VersionCommand(
            NullLogger<VersionCommand>.Instance,
            Mock.Of<IProgressReporter>(),
            new McpServerConfiguration(),
            new FakeTimeProvider());

        command.Should().NotThrow();
    }

    [Fact]
    public void It_refuses_a_missing_clock()
    {
        var command = () => new VersionCommand(
            NullLogger<VersionCommand>.Instance,
            Mock.Of<IProgressReporter>(),
            new McpServerConfiguration(),
            null!);

        command.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }
}
