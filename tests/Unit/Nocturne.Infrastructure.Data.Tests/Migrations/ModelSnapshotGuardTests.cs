using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// A model change without a migration compiles and passes every suite that builds its schema from
/// the model; only a database built from migrations lacks the column or index. This is
/// <c>dotnet ef migrations has-pending-model-changes</c> run in the unit job, against the shipped
/// snapshot and without a connection.
/// </summary>
[Trait("Category", "Unit")]
public class ModelSnapshotGuardTests
{
    [Fact]
    public void TheModelMatchesTheMigrationSnapshot()
    {
        using var context = NewContext();

        var pending = PendingOperations(context);

        pending.Should().BeEmpty(
            "the model differs from the migration snapshot; add a migration with "
            + "`dotnet ef migrations add <Name>`. Missing: {0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, pending));
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void TheGuardReadsTheSnapshotAndNotAnEmptyModel()
    {
        using var context = NewContext();
        var model = context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;

        model.Should().NotBeNull();
        model!.GetEntityTypes().Should().NotBeEmpty();
    }

    [Fact]
    public void TheDifferDescribesTablesAndIndexes()
    {
        using var context = NewContext();

        var pending = DifferOperations(context, null);

        pending.Should().Contain(p => p.StartsWith("CreateTableOperation ", StringComparison.Ordinal));
        pending.Should().Contain(p => p.StartsWith("CreateIndexOperation ", StringComparison.Ordinal));
    }

    private static NocturneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(
                "Host=unreachable.invalid;Database=none;Username=none;Password=none",
                npgsql => npgsql.UseNocturneMigrations())
            .Options;

        return new NocturneDbContext(options);
    }

    private static IReadOnlyList<string> PendingOperations(DbContext context)
    {
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        return DifferOperations(context, snapshot);
    }

    private static IReadOnlyList<string> DifferOperations(DbContext context, IModel? snapshot)
    {
        var differ = context.GetService<IMigrationsModelDiffer>();
        var runtime = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var source = snapshot is null
            ? null
            : context.GetService<IModelRuntimeInitializer>().Initialize(snapshot).GetRelationalModel();

        return differ.GetDifferences(source, runtime).Select(Describe).ToList();
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        CreateTableOperation o => $"{nameof(CreateTableOperation)} {o.Name}",
        DropTableOperation o => $"{nameof(DropTableOperation)} {o.Name}",
        AddColumnOperation o => $"{nameof(AddColumnOperation)} {o.Table}.{o.Name}",
        DropColumnOperation o => $"{nameof(DropColumnOperation)} {o.Table}.{o.Name}",
        AlterColumnOperation o => $"{nameof(AlterColumnOperation)} {o.Table}.{o.Name}",
        RenameColumnOperation o => $"{nameof(RenameColumnOperation)} {o.Table}.{o.Name}",
        CreateIndexOperation o => $"{nameof(CreateIndexOperation)} {o.Table}.{o.Name}",
        DropIndexOperation o => $"{nameof(DropIndexOperation)} {o.Table}.{o.Name}",
        RenameIndexOperation o => $"{nameof(RenameIndexOperation)} {o.Table}.{o.Name}",
        AddForeignKeyOperation o => $"{nameof(AddForeignKeyOperation)} {o.Table}.{o.Name}",
        DropForeignKeyOperation o => $"{nameof(DropForeignKeyOperation)} {o.Table}.{o.Name}",
        AddPrimaryKeyOperation o => $"{nameof(AddPrimaryKeyOperation)} {o.Table}.{o.Name}",
        DropPrimaryKeyOperation o => $"{nameof(DropPrimaryKeyOperation)} {o.Table}.{o.Name}",
        AddUniqueConstraintOperation o => $"{nameof(AddUniqueConstraintOperation)} {o.Table}.{o.Name}",
        DropUniqueConstraintOperation o => $"{nameof(DropUniqueConstraintOperation)} {o.Table}.{o.Name}",
        AddCheckConstraintOperation o => $"{nameof(AddCheckConstraintOperation)} {o.Table}.{o.Name}",
        DropCheckConstraintOperation o => $"{nameof(DropCheckConstraintOperation)} {o.Table}.{o.Name}",
        _ => operation.GetType().Name,
    };
}
