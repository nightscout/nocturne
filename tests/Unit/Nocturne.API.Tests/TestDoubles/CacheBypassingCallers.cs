using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// The callers <see cref="Nocturne.Core.Contracts.Multitenancy.ICategoryReadContext.BypassesTenantCaches"/>
/// keeps out of a tenant-keyed cache, for theories that pin each cache site to it.
/// </summary>
internal static class CacheBypassingCallers
{
    public const string ClampedMember = "clamped member";
    public const string Share = "share";
    public const string FullHistoryShare = "full-history share";

    public static TheoryData<string> All => [ClampedMember, Share, FullHistoryShare];

    public static CategoryReadContext Context(string caller)
    {
        var context = new CategoryReadContext();
        Become(context, caller);
        return context;
    }

    public static void Become(CategoryReadContext context, string caller)
    {
        switch (caller)
        {
            case ClampedMember:
                context.ClampMemberHistory();
                break;
            case Share:
                context.MarkShare();
                break;
            case FullHistoryShare:
                context.MarkShare();
                context.SetFullHistory(true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caller), caller, null);
        }
    }
}
