using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class FoodEntityConfiguration : IEntityTypeConfiguration<FoodEntity>
{
    public void Configure(EntityTypeBuilder<FoodEntity> entity)
    {
        entity.HasIndex(f => f.Name).HasDatabaseName("ix_foods_name");

        entity.HasIndex(f => f.Type).HasDatabaseName("ix_foods_type");

        entity
            .HasIndex(f => f.Category)
            .HasDatabaseName("ix_foods_category");

        entity
            .HasIndex(f => new { f.Type, f.Name })
            .HasDatabaseName("ix_foods_type_name");

        entity
            .HasIndex(f => f.SysCreatedAt)
            .HasDatabaseName("ix_foods_sys_created_at");

        // The v3 food history page; see the V4HistoryPagedEntities index.
        entity
            .HasIndex(f => new { f.TenantId, f.SysUpdatedAt, f.Id })
            .HasDatabaseName("ix_foods_tenant_sys_updated_at");

        entity
            .HasIndex(f => new { f.TenantId, f.ExternalSource, f.ExternalId })
            .HasDatabaseName("ix_foods_tenant_external")
            .HasFilter("external_source IS NOT NULL AND external_id IS NOT NULL")
            .IsUnique();

        NocturneDbContext.ScopeKeyToTenant(entity);

        entity.Property(f => f.Type).HasDefaultValue("food");

        entity
            .Property(f => f.Gi)
            .HasDefaultValue(GlycemicIndex.Medium)
            .HasSentinel((GlycemicIndex)0); // CLR default (0) is not a valid enum value, use it as sentinel

        entity.Property(f => f.Unit).HasDefaultValue("g");

        entity.Property(f => f.Position).HasDefaultValue(99999);
    }
}

internal sealed class ConnectorFoodEntryEntityConfiguration : IEntityTypeConfiguration<ConnectorFoodEntryEntity>
{
    public void Configure(EntityTypeBuilder<ConnectorFoodEntryEntity> entity)
    {
        entity
            .HasIndex(e => e.ConnectorSource)
            .HasDatabaseName("ix_connector_food_entries_source");

        entity
            .HasIndex(e => e.ExternalEntryId)
            .HasDatabaseName("ix_connector_food_entries_external_entry_id");

        entity
            .HasIndex(e => new { e.TenantId, e.ConnectorSource, e.ExternalEntryId })
            .HasDatabaseName("ix_connector_food_entries_tenant_source_id")
            .IsUnique();

        entity
            .HasIndex(e => e.Status)
            .HasDatabaseName("ix_connector_food_entries_status");

        entity
            .HasIndex(e => e.ConsumedAt)
            .HasDatabaseName("ix_connector_food_entries_consumed_at");

        entity
            .HasIndex(e => e.SysCreatedAt)
            .HasDatabaseName("ix_connector_food_entries_sys_created_at");

        entity
            .HasOne(e => e.Food)
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.FoodId })
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .Property(e => e.Status)
            .HasConversion<string>();

        entity
            .Property(e => e.Status)
            .HasDefaultValue(ConnectorFoodEntryStatus.Pending);
    }
}

internal sealed class TreatmentFoodEntityConfiguration : IEntityTypeConfiguration<TreatmentFoodEntity>
{
    public void Configure(EntityTypeBuilder<TreatmentFoodEntity> entity)
    {
        entity
            .HasIndex(tf => tf.CarbIntakeId)
            .HasDatabaseName("ix_treatment_foods_carb_intake_id");

        entity
            .HasIndex(tf => new { tf.TenantId, tf.FoodId })
            .HasDatabaseName("ix_treatment_foods_tenant_id_food_id");

        entity
            .HasIndex(tf => tf.SysCreatedAt)
            .HasDatabaseName("ix_treatment_foods_sys_created_at");

        entity
            .Property(tf => tf.TimeOffsetMinutes)
            .HasDefaultValue(0);

        entity
            .HasOne(tf => tf.CarbIntake)
            .WithMany()
            .HasForeignKey(tf => tf.CarbIntakeId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(tf => tf.Food)
            .WithMany()
            .HasForeignKey(tf => new { tf.TenantId, tf.FoodId })
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class UserFoodFavoriteEntityConfiguration : IEntityTypeConfiguration<UserFoodFavoriteEntity>
{
    public void Configure(EntityTypeBuilder<UserFoodFavoriteEntity> entity)
    {
        entity
            .HasIndex(f => f.UserId)
            .HasDatabaseName("ix_user_food_favorites_user_id");

        entity
            .HasIndex(f => new { f.TenantId, f.FoodId })
            .HasDatabaseName("ix_user_food_favorites_tenant_id_food_id");

        entity
            .HasIndex(f => new { f.TenantId, f.UserId, f.FoodId })
            .HasDatabaseName("ix_user_food_favorites_tenant_user_food")
            .IsUnique();

        entity
            .HasOne(f => f.Food)
            .WithMany()
            .HasForeignKey(f => new { f.TenantId, f.FoodId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
