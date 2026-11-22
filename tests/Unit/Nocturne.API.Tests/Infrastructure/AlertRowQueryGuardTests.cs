using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A notice that a rule cannot be evaluated stands with its excursion ended at <c>infinity</c>,
/// so a query that reads "not ended" or "ends in the future" as a live alert would take it for one.
/// Every query over <c>alert_excursions</c> or <c>alert_instances</c> therefore goes through
/// <see cref="AlertExcursionQueries"/>, writes, or is listed here with the reason it may see a notice.
/// </summary>
/// <remarks>
/// A query is a chain that starts at <c>AlertExcursions</c> or <c>AlertInstances</c> on a context, or
/// at <c>Set&lt;AlertExcursionEntity&gt;()</c> or <c>Set&lt;AlertInstanceEntity&gt;()</c>. A table read
/// with no method on it (handed to a <c>Join</c>, kept in a local, returned, cast) counts as an
/// unfiltered query, as does a string literal that reads or writes either table in SQL
/// (<c>FROM</c>, <c>JOIN</c>, <c>UPDATE</c> or <c>INTO</c> before the name). The guard reads syntax only,
/// so it cannot see: a table reached by navigation from another entity
/// (<c>AlertDeliveries.AlertInstance</c>, <c>AlertExcursionMutes.AlertExcursion</c>), whose query must
/// filter through the predicate's sub-queries as <c>HomeAssistantHub</c>'s catch-up does; SQL whose
/// table name is built from pieces or comes from outside the literal; and a receiver it takes for a
/// type, a capitalised name that does not end in <c>Context</c> (<c>ReportDataSources.AlertExcursions</c>,
/// a string).
/// </remarks>
[Trait("Category", "Unit")]
public class AlertRowQueryGuardTests
{
    private static readonly HashSet<string> Tables = ["AlertExcursions", "AlertInstances"];

    private static readonly HashSet<string> Predicates =
    [
        nameof(AlertExcursionQueries.OfCondition),
        nameof(AlertExcursionQueries.StandingNotices),
        nameof(AlertExcursionQueries.OfStandingNotices),
    ];

    private static readonly HashSet<string> Writes = ["Add", "AddRange", "Attach", "Remove", "RemoveRange", "Update"];

    private static readonly HashSet<string> Entities = ["AlertExcursionEntity", "AlertInstanceEntity"];

    private static readonly Regex Sql = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?:""?public""?\.)?""?(?:alert_excursions|alert_instances)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Members that query the tables unfiltered, keyed by file and member, with why a notice is safe there.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["src/API/Nocturne.API/Services/Alerts/AlertDeliveryService.cs:AlertDeliveryService.SendAsync"] =
            "reads the snooze of the instance being dispatched, which may be a notice's",
        ["src/API/Nocturne.API/Services/Seeding/SampleDataSeeder.cs:SampleDataSeeder.SeedAlertsAsync"] =
            "purges every row of the seeded rules, notices included",
        ["src/API/Nocturne.API/Services/SetupHub/AlertDeliveryCheck.cs:AlertDeliveryCheck.VerifiedAsync"] =
            "reads test fires only (IsTest); a notice is never one",
        ["src/API/Nocturne.API/Services/SetupHub/AlertSetupService.cs:AlertSetupService.ConfirmReceivedAsync"] =
            "reads test fires only (IsTest); a notice is never one",
        ["src/API/Nocturne.API/Services/SetupHub/AlertSetupService.cs:AlertSetupService.StarterTests"] =
            "reads test fires only (IsTest); a notice is never one",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.GetInstancesForExcursionAsync"] =
            "the resolution of a named excursion, which resolves a notice as it resolves an excursion",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.EnsureInstanceAsync"] =
            "the instance of a named excursion's opening",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.TakeUpOwedTransitionAsync"] =
            "writes a named excursion's owed work",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.SettleTransitionAsync"] =
            "writes a named excursion's owed work; a notice's opening owes its dispatch as an excursion's does",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.GetExcursionRuleAsync"] =
            "reads a named excursion's rule and whether it is a notice",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.ResolveInstancesForExcursionAsync"] =
            "the resolution of a named excursion, which resolves a notice as it resolves an excursion",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.UpdateInstanceAsync"] =
            "writes a named instance",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.DestinationsForExcursion"] =
            "the recipients a named excursion was delivered to, which a notice's resolution clears too",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertRepository.cs:AlertRepository.MarkInstanceSuppressedAsync"] =
            "writes a named instance; Do Not Disturb silences a notice as it silences an excursion",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertTrackerRepository.cs:AlertTrackerRepository.GetExcursionAsync"] =
            "the engine's own excursion, named by the tracker state; never a notice",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertTrackerRepository.cs:AlertTrackerRepository.CloseExcursionAsync"] =
            "the engine's own excursion, named by the tracker state; never a notice",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertTrackerRepository.cs:AlertTrackerRepository.SetHysteresisStartedAsync"] =
            "the engine's own excursion, named by the tracker state; never a notice",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/AlertTrackerRepository.cs:AlertTrackerRepository.ClearHysteresisAsync"] =
            "the engine's own excursion, named by the tracker state; never a notice",
    };

    [Fact]
    public void EveryQueryOverTheAlertRowsGoesThroughThePredicateOrIsListed()
    {
        Unfiltered().Where(member => !Allowed.ContainsKey(member)).Distinct()
            .Should().BeEmpty("a standing notice ends in the future and would read as a live alert; " +
                              "filter the query with AlertExcursionQueries or list the member with a reason");
    }

    [Fact]
    public void EveryListedMemberStillQueriesTheRowsUnfiltered()
    {
        var unfiltered = Unfiltered().ToHashSet();
        Allowed.Keys.Where(member => !unfiltered.Contains(member))
            .Should().BeEmpty("an allowance outlives the query it excuses only by mistake");
    }

    [Theory]
    [InlineData("class C { void M() { db.AlertExcursions.Where(e => e.EndedAt == null).ToList(); } }", "f.cs:C.M")]
    [InlineData("class C { void M() { db.AlertInstances.AsNoTracking().OfCondition().ToList(); } }", null)]
    [InlineData("class C { void M() { db.AlertExcursions.StandingNotices().Any(); } }", null)]
    [InlineData("class C { void M() { db.AlertExcursions.Add(e); } }", null)]
    [InlineData("class C { string[] S = [ReportDataSources.AlertExcursions]; }", null)]
    [InlineData("class C { void M() => q.Join(context.AlertExcursions, a => a, b => b, (a, b) => a); }", "f.cs:C.M")]
    [InlineData("class C { void M() => q.Join(context.AlertExcursions.OfCondition(), a => a, b => b, (a, b) => a); }", null)]
    [InlineData("class C { void M() => q.Join(context.AlertExcursions.AsNoTracking(), a => a, b => b, (a, b) => a); }", "f.cs:C.M")]
    [InlineData("class C { void M() { var rows = db.AlertExcursions; rows.Where(e => e.EndedAt == null).ToList(); } }", "f.cs:C.M")]
    [InlineData("class C { IQueryable<AlertInstanceEntity> Rows => lease.Context.AlertInstances; }", "f.cs:C.Rows")]
    [InlineData("class C { void M() { var rows = (IQueryable<AlertExcursionEntity>)db.AlertExcursions; } }", "f.cs:C.M")]
    [InlineData("class C { void M() { db.Set<AlertExcursionEntity>().Where(e => e.EndedAt == null).ToList(); } }", "f.cs:C.M")]
    [InlineData("class C { void M() { var rows = db.Set<AlertInstanceEntity>(); } }", "f.cs:C.M")]
    [InlineData("class C { void M() { db.Set<AlertInstanceEntity>().OfCondition().ToList(); } }", null)]
    [InlineData("class C { void M() { db.Set<AlertExcursionEntity>().Add(e); } }", null)]
    [InlineData("class C { void M() { db.Set<TreatmentEntity>().ToList(); } }", null)]
    [InlineData("class C { void M() => db.Database.SqlQueryRaw<Guid>(\"SELECT id FROM alert_excursions WHERE ended_at IS NULL\"); }", "f.cs:C.M")]
    [InlineData("class C { void M() => db.Database.ExecuteSql($\"UPDATE alert_instances SET resolved_at = {now}\"); }", "f.cs:C.M")]
    [InlineData("class C { string N = \"alert_excursions\"; }", null)]
    public void FindsAnUnfilteredQuery(string source, string? expected) =>
        Unfiltered("f.cs", CSharpSyntaxTree.ParseText(source)).SingleOrDefault().Should().Be(expected);

    private static IEnumerable<string> Unfiltered() =>
        RepoSources.Src
            .Where(source => !source.Path.Contains("/Migrations/", StringComparison.Ordinal))
            .SelectMany(source => Unfiltered(source.Path, source.Tree));

    /// <summary>
    /// The reviewer's alias: the clock face's read kept in a local before it is filtered, with no
    /// predicate. A guard that matched only a method chained on the table passed it.
    /// </summary>
    [Fact]
    public void TheGuardSeesATableKeptInALocal()
    {
        const string source = """
            class ClockFaceAlertsController
            {
                async Task GetAlerts()
                {
                    var rows = db.AlertExcursions;
                    var excursions = await rows.AsNoTracking()
                        .Where(e => e.EndedAt == null || e.EndedAt > now)
                        .ToListAsync(ct);
                }
            }
            """;

        Unfiltered("f.cs", CSharpSyntaxTree.ParseText(source)).Should().Equal("f.cs:ClockFaceAlertsController.GetAlerts");
    }

    /// <summary>The reviewer's <c>Set&lt;T&gt;</c>: the tenant overview read through the generic set.</summary>
    [Fact]
    public void TheGuardSeesAGenericSet()
    {
        const string source = """
            class TenantOverviewService
            {
                async Task OverviewAsync()
                {
                    var active = await db.Set<AlertExcursionEntity>().AsNoTracking()
                        .Include(e => e.AlertRule)
                        .Where(e => e.EndedAt == null || e.EndedAt > now)
                        .ToListAsync(ct);
                }
            }
            """;

        Unfiltered("f.cs", CSharpSyntaxTree.ParseText(source)).Should().Equal("f.cs:TenantOverviewService.OverviewAsync");
    }

    private static IEnumerable<string> Unfiltered(string path, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var tables = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => Tables.Contains(access.Name.Identifier.ValueText) && !TypeReceiver(access.Expression))
            .Cast<ExpressionSyntax>()
            .Concat(root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(GenericSet));
        var queries = tables
            .Where(table => Chain(table) is var calls
                            && (calls.Count == 0 || !Writes.Contains(calls[0]))
                            && !calls.Any(Predicates.Contains));
        var sql = root.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken)
                            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                            || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                            || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            .Where(token => Sql.IsMatch(token.ValueText))
            .Select(token => token.Parent!);

        return queries.Cast<SyntaxNode>().Concat(sql)
            .Select(node => $"{path}:{Member(node)}")
            .Distinct();
    }

    /// <summary><c>Set&lt;AlertExcursionEntity&gt;()</c> or <c>Set&lt;AlertInstanceEntity&gt;()</c>, on a receiver or bare.</summary>
    private static bool GenericSet(InvocationExpressionSyntax call)
    {
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            GenericNameSyntax generic => generic,
            _ => null,
        };
        return name is { Identifier.ValueText: "Set", TypeArgumentList.Arguments: [var entity] }
               && Entities.Contains(entity.ToString().Split('.')[^1]);
    }

    /// <summary>A receiver the guard takes for a type: a capitalised name that does not end in <c>Context</c>.</summary>
    private static bool TypeReceiver(ExpressionSyntax receiver) =>
        receiver is IdentifierNameSyntax { Identifier.ValueText: var name }
        && char.IsUpper(name[0])
        && !name.EndsWith("Context", StringComparison.Ordinal);

    /// <summary>The methods called on the table in the query it starts.</summary>
    private static List<string> Chain(SyntaxNode table)
    {
        var calls = new List<string>();
        var node = table;
        while (node.Parent is MemberAccessExpressionSyntax access && access.Expression == node)
        {
            if (access.Parent is InvocationExpressionSyntax call && call.Expression == access)
            {
                calls.Add(access.Name.Identifier.ValueText);
                node = call;
            }
            else
            {
                node = access;
            }
        }
        return calls;
    }

    private static string Member(SyntaxNode node)
    {
        var member = node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
            or LocalFunctionStatementSyntax or FieldDeclarationSyntax);
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            LocalFunctionStatementSyntax f => f.Identifier.ValueText,
            FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.ValueText,
            _ => "?",
        };
        var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "?";
        return $"{type}.{name}";
    }
}
