using System.Reflection;
using Nocturne.API.OpenApi;
using Nocturne.API.Tests.Authorization;
using Nocturne.API.Tests.Controllers.InvalidationKeyFixtures;
using Nocturne.API.Tests.Infrastructure;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// openapi-remote-codegen resolves each <c>Invalidates</c> entry against the generated OpenAPI
/// document and drops one that names no query without a word, so the command quietly stops
/// refreshing it. A bare name resolves only to a query in the declaring controller's own tag
/// (<see cref="ControllerNameTagOperationProcessor.TagFor"/>); a name with an underscore is a full
/// operationId, NSwag's <c>{controller}_{action}</c>, and may name a query on any controller.
/// </summary>
[Trait("Category", "Unit")]
public class RemoteInvalidationKeyGuardTests
{
    private static readonly Type[] FixtureControllers =
    [
        typeof(DeclaringController),
        typeof(OtherController),
        typeof(InvalidationKeyFixtures.First.SharedTagController),
        typeof(InvalidationKeyFixtures.Second.SharedTagController),
    ];

    [Fact]
    public void EveryInvalidationKey_ResolvesToAQuery()
    {
        var controllers = NocturneDocumentControllers();

        controllers.Should().NotBeEmpty();

        UnresolvedKeys(controllers).Should().BeEmpty(
            "a key that resolves to no [RemoteQuery] is dropped by the generator and never refreshed");
    }

    [Fact]
    public void NoRemoteActionName_EndsInAsync()
    {
        RemoteActionsEndingInAsync(NocturneDocumentControllers()).Should().BeEmpty(
            "ASP.NET strips the Async suffix from the operationId but the guard keys queries by method name");
    }

    [Fact]
    public void TheAsyncSuffixAssertion_ReportsEveryKindOfRemoteAction()
    {
        RemoteActionsEndingInAsync(ControllerActionReflection.GetControllers([typeof(SuffixedController)]))
            .Should().BeEquivalentTo(
            [
                $"{nameof(SuffixedController)}.{nameof(SuffixedController.ReadAsync)}",
                $"{nameof(SuffixedController)}.{nameof(SuffixedController.WriteAsync)}",
                $"{nameof(SuffixedController)}.{nameof(SuffixedController.SubmitAsync)}",
            ]);
    }

    [Fact]
    public void ABareKey_ResolvesToAQueryInTheDeclaringControllersTagOnly()
    {
        UnresolvedKeys(ControllerActionReflection.GetControllers(FixtureControllers).ToList())
            .Where(key => key.StartsWith($"{nameof(DeclaringController)}.{nameof(DeclaringController.Bare)}:"))
            .Should().BeEquivalentTo(
            [
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.Bare)}: {nameof(OtherController.OtherQuery)}",
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.Bare)}: {nameof(DeclaringController.Write)}",
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.Bare)}: Missing",
            ]);
    }

    [Fact]
    public void ABareKey_ResolvesToAQueryOnAnotherControllerSharingTheTag()
    {
        UnresolvedKeys(ControllerActionReflection.GetControllers(FixtureControllers).ToList())
            .Should().NotContain(key => key.StartsWith(
                $"{nameof(InvalidationKeyFixtures.First.SharedTagController)}.{nameof(InvalidationKeyFixtures.First.SharedTagController.Write)}:"));
    }

    [Fact]
    public void AQualifiedKey_ResolvesToAQueryOnAnyControllerByItsOperationId()
    {
        UnresolvedKeys(ControllerActionReflection.GetControllers(FixtureControllers).ToList())
            .Should().NotContain(key => key.StartsWith($"{nameof(DeclaringController)}.{nameof(DeclaringController.Qualified)}:"));
    }

    [Fact]
    public void AQualifiedKey_NamingAWriteOrAnUnknownActionIsUnresolved()
    {
        UnresolvedKeys(ControllerActionReflection.GetControllers(FixtureControllers).ToList())
            .Where(key => key.StartsWith($"{nameof(DeclaringController)}.{nameof(DeclaringController.BadlyQualified)}:"))
            .Should().BeEquivalentTo(
            [
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.BadlyQualified)}: Declaring_Write",
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.BadlyQualified)}: Other_Nothing",
            ]);
    }

    [Fact]
    public void ARemoteFormKey_IsGuardedLikeARemoteCommandKey()
    {
        UnresolvedKeys(ControllerActionReflection.GetControllers(FixtureControllers).ToList())
            .Should().Contain(
                $"{nameof(DeclaringController)}.{nameof(DeclaringController.Submit)}: Missing");
    }

    private static List<Type> NocturneDocumentControllers() =>
        ControllerActionReflection.GetControllers()
            .Where(type => ApiDocumentMembership.InNocturneDocument(type.Namespace ?? string.Empty))
            .ToList();

    private static List<string> UnresolvedKeys(IReadOnlyCollection<Type> controllers)
    {
        var queries = controllers
            .SelectMany(controller => ControllerActionReflection.GetActionMethods(controller)
                .Where(method => RemoteAttributes.Query(method) is not null)
                .Select(method => (Controller: controller, Method: method)))
            .ToList();

        var unresolved = new List<string>();

        foreach (var controller in controllers)
        {
            foreach (var method in ControllerActionReflection.GetActionMethods(controller))
            {
                foreach (var key in InvalidatesOf(method))
                {
                    var resolves = key.Contains('_')
                        ? queries.Any(query => OperationId(query.Controller, query.Method) == key)
                        : queries.Any(query =>
                            ControllerNameTagOperationProcessor.TagFor(query.Controller)
                                == ControllerNameTagOperationProcessor.TagFor(controller)
                            && query.Method.Name == key);

                    if (!resolves)
                        unresolved.Add($"{controller.Name}.{method.Name}: {key}");
                }
            }
        }

        return unresolved;
    }

    private static IEnumerable<string> RemoteActionsEndingInAsync(IEnumerable<Type> controllers) =>
        controllers
            .SelectMany(controller => ControllerActionReflection.GetActionMethods(controller)
                .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)
                                 && (RemoteAttributes.Query(method) is not null
                                     || method.GetCustomAttribute<RemoteCommandAttribute>() is not null
                                     || method.GetCustomAttribute<RemoteFormAttribute>() is not null))
                .Select(method => $"{controller.Name}.{method.Name}"));

    private static IEnumerable<string> InvalidatesOf(MethodInfo method) =>
        (method.GetCustomAttribute<RemoteCommandAttribute>()?.Invalidates ?? [])
            .Concat(method.GetCustomAttribute<RemoteFormAttribute>()?.Invalidates ?? []);

    private static string OperationId(Type controller, MethodInfo action)
    {
        var name = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? controller.Name[..^"Controller".Length]
            : controller.Name;

        return $"{name}_{action.Name}";
    }
}
