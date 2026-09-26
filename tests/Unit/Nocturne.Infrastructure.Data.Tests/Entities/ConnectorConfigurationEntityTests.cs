namespace Nocturne.Infrastructure.Data.Tests.Entities;

/// <summary>
/// The connector name reaches this entity in two spellings — the PascalCase name off a connector's
/// registration and the lowercase id out of an API route — and the unique index over
/// (connector_name, tenant_id) is case-sensitive. Folding here rather than in each caller is what
/// makes a second spelling unreachable, so it has to hold for a writer that never went through
/// <c>ConnectorConfigurationService</c>.
/// </summary>
public class ConnectorConfigurationEntityTests
{
    [Theory]
    [InlineData("CareLink", "carelink")]
    [InlineData("LibreLinkUp", "librelinkup")]
    [InlineData("carelink", "carelink")]
    public void ConnectorName_IsStoredCanonical(string written, string expected)
    {
        var entity = new ConnectorConfigurationEntity { ConnectorName = written };

        entity.ConnectorName.Should().Be(expected);
    }

    [Fact]
    public void ConnectorName_DefaultsToEmpty()
    {
        new ConnectorConfigurationEntity().ConnectorName.Should().BeEmpty();
    }
}
