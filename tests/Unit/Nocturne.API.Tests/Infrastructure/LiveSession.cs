using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A session caller whose session is live, as <see cref="CallerSessionLiveness"/> judges it: a
/// controller test that signs in with a session names <see cref="Id"/> as its session and registers
/// <see cref="AddLiveSession"/>, so an action that mints finds the session live.
/// </summary>
public static class LiveSession
{
    /// <summary>The session a test caller signed in with.</summary>
    public const string Id = "live-session";

    /// <summary>Registers a <see cref="CallerSessionLiveness"/> that finds <see cref="Id"/> live.</summary>
    public static IServiceCollection AddLiveSession(this IServiceCollection services) =>
        services.AddSingleton(Liveness());

    /// <summary>
    /// Registers a subject store that finds every subject active, for an action whose route is
    /// served without a tenant and so reads the caller's own row.
    /// </summary>
    public static IServiceCollection AddActiveSubjects(this IServiceCollection services)
    {
        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(s => s.GetSignInStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: true, IsPlatformAdmin: false));
        return services.AddSingleton(subjects.Object);
    }

    /// <summary>A <see cref="CallerSessionLiveness"/> that finds <see cref="Id"/> live.</summary>
    public static CallerSessionLiveness Liveness()
    {
        var sessions = new Mock<IFirstPartyTokenRepository>();
        sessions
            .Setup(s => s.HasLiveSessionAsync(It.IsAny<Guid>(), Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new CallerSessionLiveness(sessions.Object, TimeProvider.System);
    }
}
