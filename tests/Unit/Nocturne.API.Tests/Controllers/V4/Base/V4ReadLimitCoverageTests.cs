using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Controllers.V4.Base;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// Guards that every V4 read taking a caller-supplied page size runs it through
/// <see cref="V4ReadLimits"/> before it reaches a repository or service.
/// </summary>
/// <remarks>
/// The per-route boundary tests pin the behaviour of the routes that exist today; this pins the
/// rule, so a new V4 list route that declares its own <c>limit</c> or <c>count</c> and forgets the
/// clamp fails here rather than shipping an unbounded read. It reads IL, so it can tell that an
/// action reaches <see cref="V4ReadLimits"/> but not which of its parameters it clamped — the
/// boundary tests are what pin that.
/// </remarks>
public class V4ReadLimitCoverageTests
{
    private const string V4Namespace = "Nocturne.API.Controllers.V4";

    /// <summary>
    /// Parameter names that carry a caller-supplied page size on this surface. <c>offset</c>,
    /// <c>skip</c> and <c>page</c> are not listed: they are meaningless without a page size, so an
    /// action that declares one declares the other, and the page-size name is what selects the
    /// action.
    /// </summary>
    private static readonly string[] PageSizeParameterNames = ["limit", "count", "pageSize"];

    /// <summary>
    /// Actions exempt from the rule, keyed <c>Controller.Action</c>, with what bounds them instead.
    /// <see cref="ExemptActions_StillNameACandidate"/> keeps the keys live.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExemptActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Clamps to a ceiling of its own, an order of magnitude below V4ReadLimits.MaxPageSize.
            // A monolithic legacy-shaped profile carries every schedule array inline, so the page
            // is far heavier per record than the rest of this surface.
            ["ProfileController.GetProfileRecords"] = "clamps to its own tighter ceiling",
        };

    [Fact]
    public void EveryV4ReadTakingAPageSize_ClampsItThroughV4ReadLimits()
    {
        var candidates = FindCandidates();
        var violations = candidates
            .Where(c => !ExemptActions.ContainsKey(Key(c)))
            .Where(c => !ClampsThroughV4ReadLimits(c.Action))
            .Select(Key)
            .ToList();

        // A sweep that discovers nothing would pass while guarding nothing.
        candidates.Should().HaveCountGreaterThan(20,
            "the scan should discover the V4 routes that declare their own page size");

        violations.Should().BeEmpty(
            $"a V4 read must clamp its page size through {nameof(V4ReadLimits)} before it reaches a " +
            "repository or service, or a single request can ask for the whole table. Unclamped: " +
            string.Join("; ", violations));
    }

    [Fact]
    public void ExemptActions_StillNameACandidate()
    {
        var candidates = FindCandidates().Select(Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, reason) in ExemptActions)
        {
            candidates.Should().Contain(key,
                $"{key} is exempted ({reason}) but the scan no longer finds it — remove the exemption");
        }
    }

    private static string Key((Type Controller, MethodInfo Action) candidate) =>
        $"{candidate.Controller.Name}.{candidate.Action.Name}";

    /// <summary>
    /// The V4 actions declaring a page-size parameter of their own.
    /// </summary>
    private static List<(Type Controller, MethodInfo Action)> FindCandidates() =>
        typeof(V4ReadLimits).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                        && (ns == V4Namespace || ns.StartsWith($"{V4Namespace}.", StringComparison.Ordinal)))
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t.IsClass)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any())
                .Where(DeclaresAPageSize)
                .Select(m => (Controller: t, Action: m)))
            .ToList();

    private static bool DeclaresAPageSize(MethodInfo action) =>
        action.GetParameters().Any(p =>
            (p.ParameterType == typeof(int) || p.ParameterType == typeof(int?))
            && PageSizeParameterNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="action"/> reaches <see cref="V4ReadLimits"/>, following calls into
    /// its own async state machine and into helpers it inherits (the shared base controllers clamp
    /// in a protected helper rather than in the action body).
    /// </summary>
    private static bool ClampsThroughV4ReadLimits(MethodInfo action)
    {
        var visited = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>();
        pending.Enqueue(action);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
                continue;

            foreach (var body in Bodies(current))
            {
                foreach (var called in CalledMethods(body))
                {
                    if (called.DeclaringType == typeof(V4ReadLimits))
                        return true;

                    if (DeclaredWithin(called.DeclaringType, action.DeclaringType!))
                        pending.Enqueue(called);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The method itself plus, for an async method, the <c>MoveNext</c> its body was rewritten
    /// into — where every call an async action makes actually lives.
    /// </summary>
    private static IEnumerable<MethodBase> Bodies(MethodBase method)
    {
        yield return method;

        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = stateMachine?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (moveNext is not null)
            yield return moveNext;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the controller, one of its base controllers, or a
    /// type nested in either — the reach within which a clamp still counts as the action's own.
    /// </summary>
    private static bool DeclaredWithin(Type? candidate, Type controller)
    {
        var outer = candidate;
        while (outer?.DeclaringType is not null)
            outer = outer.DeclaringType;

        if (outer is null)
            return false;

        if (outer.IsGenericType)
            outer = outer.GetGenericTypeDefinition();

        for (var type = controller; type is not null; type = type.BaseType)
        {
            var self = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            if (self == outer)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The methods called from <paramref name="method"/>'s IL, found by resolving the token after
    /// every <c>call</c> and <c>callvirt</c> opcode byte.
    /// </summary>
    /// <remarks>
    /// Mirrors the same walk in <c>HubAuthorizationFilterTests</c>. A byte that is not an
    /// instruction boundary yields a token that does not resolve and is skipped, so the result is a
    /// superset of the real calls.
    /// </remarks>
    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        const byte Call = 0x28;
        const byte CallVirt = 0x6F;

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;

        var typeArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (Call or CallVirt))
                continue;

            MethodBase? called = null;
            try
            {
                called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), typeArguments, null);
            }
            catch (Exception)
            {
                // Not an instruction boundary, or a token needing generic context to resolve.
            }

            if (called is not null)
                yield return called;
        }
    }
}
