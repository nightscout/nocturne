using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.Authentication;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unlinking an OIDC identity drops a primary sign-in factor, and the count of those is reported by
/// <see cref="PasskeyController.ListCredentials"/> — not by this controller's own read. The account
/// page decides from that count whether a passkey's Remove is offered at all, so a refresh list
/// naming only <see cref="OidcController.GetLinkedIdentities"/> leaves the count stale until the next
/// full load and the page keeps offering a Remove the server answers 409 <c>last_factor</c>.
/// </summary>
[Trait("Category", "Unit")]
public class OidcInvalidationTests
{
    [Theory]
    [InlineData(nameof(OidcController.GetLinkedIdentities))]
    [InlineData("Passkey_ListCredentials")]
    public void UnlinkingAnIdentity_RefreshesTheReadsThatReportIt(string read)
    {
        Command(nameof(OidcController.UnlinkIdentity))
            .Invalidates.Should()
            .Contain(read);
    }

    /// <summary>
    /// Only a query can be refreshed, and a read on another controller has to be named the way the
    /// OpenAPI document names it — NSwag's <c>{controller}_{action}</c> operationId — because a bare
    /// name resolves only within the declaring operation's own tag and is otherwise dropped in
    /// silence. Resolving each entry back to a method proves the spelling still lands on something.
    /// </summary>
    [Fact]
    public void EveryRefreshedRead_IsAQueryTheCommandCanName()
    {
        var reads = Command(nameof(OidcController.UnlinkIdentity)).Invalidates;

        reads.Should().NotBeEmpty();

        foreach (var read in reads)
        {
            Resolve(read).Should().NotBeNull($"'{read}' should name a method on a controller");
            Resolve(read)!.GetCustomAttribute<RemoteQueryAttribute>().Should()
                .NotBeNull($"'{read}' should name a query, since only a query can be refreshed");
        }
    }

    /// <summary>
    /// Resolves one <c>Invalidates</c> entry to the action it names: a bare name against the
    /// declaring controller, a <c>{controller}_{action}</c> operationId against the controller that
    /// prefix belongs to (<see cref="Nocturne.API.OpenApi.ControllerNameTagOperationProcessor"/>
    /// derives both the tag and the prefix from the controller's type name).
    /// </summary>
    private static MethodInfo? Resolve(string read)
    {
        var separator = read.IndexOf('_');
        if (separator < 0)
        {
            return typeof(OidcController).GetMethod(read);
        }

        var controller = typeof(OidcController).Assembly
            .GetType($"{typeof(OidcController).Namespace}.{read[..separator]}Controller");

        return controller?.GetMethod(read[(separator + 1)..]);
    }

    private static RemoteCommandAttribute Command(string write) =>
        typeof(OidcController).GetMethod(write)!
            .GetCustomAttribute<RemoteCommandAttribute>()!;
}
