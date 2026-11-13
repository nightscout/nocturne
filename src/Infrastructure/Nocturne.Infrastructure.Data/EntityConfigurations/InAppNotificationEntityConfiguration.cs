using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class InAppNotificationEntityConfiguration : IEntityTypeConfiguration<InAppNotificationEntity>
{
    public void Configure(EntityTypeBuilder<InAppNotificationEntity> entity)
    {
        entity
            .HasIndex(n => n.UserId)
            .HasDatabaseName("ix_in_app_notifications_user_id");

        entity
            .HasIndex(n => n.Type)
            .HasDatabaseName("ix_in_app_notifications_type");

        entity
            .HasIndex(n => n.IsArchived)
            .HasDatabaseName("ix_in_app_notifications_is_archived");

        entity
            .HasIndex(n => n.CreatedAt)
            .HasDatabaseName("ix_in_app_notifications_created_at")
            .IsDescending();

        entity
            .HasIndex(n => new { n.UserId, n.IsArchived })
            .HasDatabaseName("ix_in_app_notifications_user_archived");

        entity
            .HasIndex(n => new
            {
                n.UserId,
                n.Type,
                n.SourceId,
                n.IsArchived,
            })
            .HasDatabaseName("ix_in_app_notifications_user_type_source_archived");

        entity
            .HasIndex(n => n.SourceId)
            .HasDatabaseName("ix_in_app_notifications_source_id")
            .HasFilter("source_id IS NOT NULL");

        entity.Property(e => e.IsArchived).HasDefaultValue(false);

        entity.Property(e => e.Category).HasConversion<string>();
        entity.Property(e => e.Urgency).HasConversion<string>();
        entity.Property(e => e.ArchiveReason).HasConversion<string>();
    }
}
