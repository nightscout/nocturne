using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>Maps a <see cref="SubjectEntity"/> to the record every subject read returns.</summary>
internal static class SubjectMapper
{
    public static SubjectRecord ToRecord(SubjectEntity subject, IReadOnlyList<Role> roles) =>
        new(
            subject.Id,
            subject.Name,
            subject.Username,
            subject.Email,
            subject.EmailConfirmedAt,
            subject.Notes,
            subject.IsActive,
            subject.IsSystemSubject,
            subject.IsDemoSubject,
            subject.IsPlatformAdmin,
            subject.ApprovalStatus,
            subject.AccessRequestMessage,
            subject.PreferredLanguage,
            subject.Preferences,
            subject.AvatarUrl,
            subject.CreatedAt,
            subject.UpdatedAt,
            subject.LastLoginAt,
            roles);
}
