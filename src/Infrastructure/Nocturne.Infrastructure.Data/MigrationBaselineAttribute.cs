namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Marks the first migration as a baseline: every migration up to the last one of
/// <see cref="Release"/>, squashed by <c>scripts/squash-migrations.cs</c> into the script pg_dump
/// takes of a database migrated that far. The baseline keeps that last migration's id, so a
/// database that applied it already has the baseline recorded and skips it.
/// <para>
/// A database migrated by an older release has neither the baseline nor the migrations it
/// replaced, and <see cref="Extensions.DatabaseInitializationExtensions.EnsureReachesBaselineAsync"/>
/// refuses it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MigrationBaselineAttribute(string release) : Attribute
{
    /// <summary>The release the baseline squashes up to, and the oldest one this build upgrades from.</summary>
    public string Release { get; } = release;
}
