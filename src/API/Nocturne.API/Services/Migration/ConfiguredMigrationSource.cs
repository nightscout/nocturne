namespace Nocturne.API.Services.Migration;

/// <summary>
/// The migration source the instance's settings name (<c>MIGRATION_MODE</c>, <c>MIGRATION_NS_URL</c>,
/// <c>MIGRATION_NS_API_SECRET</c>, <c>MIGRATION_MONGO_*</c>). It is the operator's, and its URL can
/// carry the credential in its query, so only a caller with standing over the instance
/// (<see cref="Auth.InstanceStanding.HeldByAsync"/>) is told of it or may import from it.
/// </summary>
internal sealed record ConfiguredMigrationSource(
    MigrationMode Mode,
    string? NightscoutUrl,
    bool HasApiSecret,
    string? MongoConnectionString,
    string? MongoDatabaseName)
{
    /// <summary>The source <paramref name="configuration"/> names, or null when <c>MIGRATION_MODE</c> is unset.</summary>
    public static ConfiguredMigrationSource? Read(IConfiguration configuration)
    {
        var mode = configuration["MIGRATION_MODE"];
        if (string.IsNullOrEmpty(mode))
            return null;

        return new ConfiguredMigrationSource(
            mode.Equals("MongoDb", StringComparison.OrdinalIgnoreCase) ? MigrationMode.MongoDb : MigrationMode.Api,
            configuration["MIGRATION_NS_URL"] is { Length: > 0 } url ? url : null,
            !string.IsNullOrEmpty(configuration["MIGRATION_NS_API_SECRET"]),
            configuration["MIGRATION_MONGO_CONNECTION_STRING"] is { Length: > 0 } connection ? connection : null,
            configuration["MIGRATION_MONGO_DATABASE_NAME"]);
    }
}
