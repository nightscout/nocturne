using FluentAssertions;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Verifies that the DataProtectionKeys table exists after migrations and that
/// the nocturne_app role has the CRUD access needed for the Data Protection key
/// ring to survive container restarts.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DataProtectionKeysTests
{
    private readonly RlsCompletenessFixture _fx;

    public DataProtectionKeysTests(RlsCompletenessFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task AppRole_CanInsertAndQueryDataProtectionKeys()
    {
        await using var conn = await _fx.OpenAppConnectionAsync();

        var friendlyName = $"test-key-{Guid.NewGuid():N}";
        const string xml = "<key />";

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO "DataProtectionKeys" ("FriendlyName", "Xml")
            VALUES (@name, @xml)
            """;
        AddParam(insert, "@name", friendlyName);
        AddParam(insert, "@xml", xml);
        await insert.ExecuteNonQueryAsync();

        await using var select = conn.CreateCommand();
        select.CommandText = """
            SELECT COUNT(*) FROM "DataProtectionKeys" WHERE "FriendlyName" = @name
            """;
        AddParam(select, "@name", friendlyName);
        var count = Convert.ToInt64(await select.ExecuteScalarAsync());

        count.Should().Be(1, "nocturne_app must be able to read and write DataProtectionKeys");
    }

    private static void AddParam(NpgsqlCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
