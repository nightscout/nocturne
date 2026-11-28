using FluentAssertions;
using Nocturne.API.Services.ProfileActivations;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

[Trait("Category", "Unit")]
public sealed class ProfileActivationSourceTests
{
    [Theory]
    [InlineData(null, "", "xdrip-js://rig", "xdrip-js://rig")]
    [InlineData(null, null, "xdrip-js://rig", "xdrip-js://rig")]
    [InlineData(null, "Trio", "trio", "Trio")]
    [InlineData("dexcom-connector", "", "x", "dexcom-connector")]
    [InlineData(null, "", null, "")]
    public void An_empty_app_is_read_as_absent(string? dataSource, string? app, string? enteredBy, string expected) =>
        ProfileActivationSource.KeyOf(dataSource, app, enteredBy).Should().Be(expected);
}
