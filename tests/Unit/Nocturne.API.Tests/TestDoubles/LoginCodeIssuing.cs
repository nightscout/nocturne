using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Mints a code of any purpose through the method that issues that purpose.</summary>
internal static class LoginCodeIssuing
{
    public const string Address = "a@example.test";

    public static async Task<string> IssueAnyAsync(
        this ILoginCodeService codes, NocturneDbContext db, Guid subjectId, LoginCodePurpose purpose, string email = Address)
    {
        var actor = new AuthAuditActor(null, "test");
        return LoginCodeService.EmailPurposes.Contains(purpose)
            ? (await codes.IssueEmailedAsync(db, subjectId, purpose, email, null, null, actor))!.Code
            : (await codes.IssueAsync(db, subjectId, purpose, null, null, actor)).Code;
    }
}
