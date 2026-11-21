using Nocturne.Core.Contracts.Auth;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>The two answers <see cref="IAccountLinkRoute"/> can give, for a test to name the one it runs under.</summary>
public static class AccountLinkRoutes
{
    /// <summary>No link can be sent, so a confirmed address is no way back in.</summary>
    public static IAccountLinkRoute Off { get; } = new Fixed(false);

    /// <summary>A link can be sent, so a confirmed address is a way back in.</summary>
    public static IAccountLinkRoute On { get; } = new Fixed(true);

    /// <summary>The answer for <paramref name="configured"/>.</summary>
    public static IAccountLinkRoute Of(bool configured) => configured ? On : Off;

    private sealed class Fixed(bool configured) : IAccountLinkRoute
    {
        public bool IsConfigured => configured;
    }
}
