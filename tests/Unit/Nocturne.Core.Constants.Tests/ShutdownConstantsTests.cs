using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

public class ShutdownConstantsTests
{
    [Fact]
    public void TheContainerOutwaitsTheHostByTheMargin()
    {
        ApplicationConstants.Shutdown.ContainerMarginSeconds.Should().BePositive();
        ApplicationConstants.Shutdown.ContainerStopGracePeriod.Should().Be(
            $"{ApplicationConstants.Shutdown.HostTimeoutSeconds + ApplicationConstants.Shutdown.ContainerMarginSeconds}s");
    }
}
