using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// A row of any V4 record table that the store accepts, built from the model alone, for a test that
/// has to hold every table to one rule.
/// </summary>
public static class RecordTableRows
{
    /// <summary>
    /// A new row of <paramref name="entity"/> in <paramref name="tenant"/>: every required text column
    /// filled, a jsonb one with an empty object, and every time column at <paramref name="at"/>.
    /// </summary>
    public static object New(IEntityType entity, Guid tenant, DateTime at)
    {
        var row = Activator.CreateInstance(entity.ClrType)!;
        foreach (var property in entity.GetProperties().Where(p => p.PropertyInfo is { CanWrite: true }))
        {
            var info = property.PropertyInfo!;
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                info.SetValue(row, at);
            else if (property.ClrType == typeof(string) && !property.IsNullable && info.GetValue(row) is null)
                info.SetValue(row, property.GetColumnType() == "jsonb" ? "{}" : "x");
        }

        ((ITenantScoped)row).TenantId = tenant;
        ((IIdentified)row).Id = Guid.CreateVersion7();
        return row;
    }

    /// <summary>The time columns of <paramref name="entity"/>.</summary>
    public static IEnumerable<IProperty> TimeColumns(IEntityType entity) =>
        entity.GetProperties().Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?));

    /// <summary>The row of <paramref name="entity"/> with <paramref name="id"/>, soft-deleted or not.</summary>
    public static Task<object> FindAsync(NocturneDbContext db, IEntityType entity, Guid id) =>
        (Task<object>)typeof(RecordTableRows)
            .GetMethod(nameof(FindOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entity.ClrType)
            .Invoke(null, [db, id])!;

    private static async Task<object> FindOfAsync<T>(NocturneDbContext db, Guid id) where T : class, IIdentified =>
        await db.Set<T>().IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]).AsNoTracking()
            .SingleAsync(e => e.Id == id);
}
