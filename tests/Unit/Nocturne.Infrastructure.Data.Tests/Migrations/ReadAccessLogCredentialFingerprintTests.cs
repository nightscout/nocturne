namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// The column that recorded a prefix of the credential's stored digest is dropped, not renamed: the
/// prefix is match material for the lookup that authenticates the credential, and no existing value
/// can be recomputed into the fingerprint that replaces it.
/// </summary>
[Trait("Category", "Unit")]
public class ReadAccessLogCredentialFingerprintTests
{
    private const string MigrationName = "ReadAccessLogCredentialFingerprint";

    [Fact]
    public void TheOldPrefixColumnIsDroppedRatherThanCarriedUnderTheNewName()
    {
        var up = MigrationSourceFiles.Text(MigrationName);
        up = up[..up.IndexOf("void Down(", StringComparison.Ordinal)];

        up.Should().Contain("DropColumn")
            .And.Contain("api_secret_hash_prefix",
                "a RenameColumn would leave every stored prefix readable under the new name");
        up.Should().NotContain("RenameColumn");
    }
}
