using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ChatIdentityDirectoryEntryConfiguration : IEntityTypeConfiguration<ChatIdentityDirectoryEntry>
{
    public void Configure(EntityTypeBuilder<ChatIdentityDirectoryEntry> entity)
    {
        // Chat identity directory — global (NOT tenant-scoped) routing table.
        entity.HasKey(e => e.Id);

        entity.HasIndex(e => new { e.Platform, e.PlatformUserId, e.TenantId })
            .IsUnique()
            .HasDatabaseName("ux_directory_user_tenant");

        // Labels route bot commands, so they must be unambiguous within a platform user's set
        // of links. ChatIdentityDirectoryService.CreateLinkAsync auto-suffixes a colliding
        // label before insert and retries against this index when it loses a race.
        entity.HasIndex(e => new { e.Platform, e.PlatformUserId, e.Label })
            .IsUnique()
            .HasDatabaseName("ux_directory_user_label");

        // At most one default per Discord user — partial unique index.
        entity.HasIndex(e => new { e.Platform, e.PlatformUserId })
            .IsUnique()
            .HasFilter("is_default = true")
            .HasDatabaseName("ux_directory_user_one_default");

        entity.HasIndex(e => e.TenantId).HasDatabaseName("ix_directory_tenant_id");

        // Not tenant-scoped (no RLS — the bot resolves across tenants), but the tenant
        // reference is still a real FK so deleting a tenant takes its directory rows with it.
        // Without it these rows outlive the tenant, and each one holds a chat-platform user id
        // plus the tenant's slug and display name in Label/DisplayName — so a "delete
        // everything" would leave a person's Discord/Telegram id still associated with the
        // instance they had. No navigation property: nothing should traverse tenant -> chat
        // links, this exists purely for the cascade.
        entity.HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // The directory row is what binds a chat account to a tenant, so it must not outlive
        // the subject it was issued for. Subjects are global, so this only covers deleting the
        // person entirely — losing one tenant's membership is handled where that happens.
        entity.HasOne<SubjectEntity>()
            .WithMany()
            .HasForeignKey(e => e.NocturneUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChatIdentityPendingLinkEntityConfiguration : IEntityTypeConfiguration<ChatIdentityPendingLinkEntity>
{
    public void Configure(EntityTypeBuilder<ChatIdentityPendingLinkEntity> entity)
    {
        entity.HasKey(e => e.Token);
        entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_pending_links_expires_at");
    }
}
