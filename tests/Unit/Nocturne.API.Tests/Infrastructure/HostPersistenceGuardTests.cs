using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nocturne.API.Extensions;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A concept whose persistence module is in Infrastructure.Data is reached from the web host
/// through its contract only. For each concept in <see cref="Concepts"/> this checks three things:
/// no file in the API project contains one of its entity type names or one of its
/// <c>DbSet</c>s as a member access (<c>.AlertRules</c>), doc comments and comments included; no
/// constructor, method, field or property of the API assembly carries one of its entity types;
/// and every listed entity is a type Infrastructure.Data declares. Each slice of #2129 adds its
/// concept as it lands.
/// </summary>
/// <remarks>
/// The text scan does not see an entity reached through a navigation property, so a host member
/// that holds one that way is caught only by the reflection half, and a query that goes through a
/// navigation is not caught at all. An <see cref="Exemption"/> names a file that may still use the
/// concept's <c>DbSet</c>s or types, with the reason; a later slice or follow-up deletes it.
/// </remarks>
[Trait("Category", "Unit")]
public class HostPersistenceGuardTests
{
    private sealed record Exemption(string File, string Reason);

    private sealed record Concept(string Name, string[] Entities, string[] DbSets, Exemption[] Exemptions)
    {
        public IEnumerable<Regex> Patterns =>
            Entities.Select(NamePattern).Concat(DbSets.Select(DbSetPattern));

        public bool Exempts(string path) => Exemptions.Any(e => e.File == path);
    }

    private static readonly Concept[] Concepts =
    [
        new("alert rules (#3100)",
            ["AlertRuleEntity", "AlertRuleChannelEntity"],
            ["AlertRules", "AlertRuleChannels"],
            [
                new("src/API/Nocturne.API/Controllers/V4/Monitoring/AlertInvitesController.cs",
                    "reads whether a channel exists before minting an invite; moves behind the repository in #3107"),
                new("src/API/Nocturne.API/Services/Platform/InstanceKey/UndecryptableSecretScanner.cs",
                    "reads every channel secret of the tenant for the instance-key scan; moves behind the repository in #3107"),
            ]),
        new("OAuth tokens (#3101)",
            ["OAuthAuthorizationCodeEntity", "OAuthRefreshTokenEntity", "OAuthDeviceCodeEntity"],
            ["OAuthAuthorizationCodes", "OAuthRefreshTokens", "OAuthDeviceCodes"],
            [
                new("src/API/Nocturne.API/Services/Auth/OAuthCodeCleanupService.cs",
                    "deletes the OAuth clients no grant or code names, by a subquery on the code tables; moves with OAuth client persistence in #3102"),
            ]),
    ];

    private const string Host = "src/API/Nocturne.API";

    private static readonly Assembly Api = typeof(ServiceRegistrationExtensions).Assembly;
    private static readonly Assembly Data = typeof(NocturneDbContext).Assembly;

    [Fact]
    public void NoHostFileNamesAnEntityOrDbSetOfAConcept()
    {
        var root = RepositoryRoot();
        var namers = HostFiles(root)
            .Select(path => (path, text: File.ReadAllText(Path.Combine(root, path))))
            .SelectMany(file => Concepts
                .Where(concept => !concept.Exempts(file.path))
                .SelectMany(concept => concept.Patterns)
                .Where(pattern => pattern.IsMatch(file.text))
                .Select(pattern => $"{file.path}({pattern})"))
            .ToList();

        namers.Should().BeEmpty("the host reaches a ported concept's rows through its repository contract, not its entities or DbSets");
    }

    [Fact]
    public void NoHostMemberCarriesAnEntityOfAConcept()
    {
        var entities = Concepts.SelectMany(c => c.Entities).ToHashSet(StringComparer.Ordinal);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var members = Api.GetTypes()
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Concat(t.GetFields(all).Select(f => f.FieldType))
                .Concat(t.GetProperties(all).Select(p => p.PropertyType))
                .SelectMany(EntitiesIn)
                .Where(entities.Contains)
                .Select(entity => $"{t.FullName}({entity})"))
            .Distinct()
            .ToList();

        members.Should().BeEmpty("a host member that carries an entity is persistence code outside its module");
    }

    [Fact]
    public void EveryListedEntityIsDeclaredInInfrastructureData()
    {
        var declared = Data.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var dbSets = typeof(NocturneDbContext).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Concepts.SelectMany(c => c.Entities).Should().OnlyContain(entity => declared.Contains(entity), "a renamed entity would drop out of the guard");
        Concepts.SelectMany(c => c.DbSets).Should().OnlyContain(set => dbSets.Contains(set), "a renamed DbSet would drop out of the guard");
    }

    [Fact]
    public void EveryExemptFileExistsAndUsesItsConcept()
    {
        var root = RepositoryRoot();
        var exemptions = Concepts.SelectMany(c => c.Exemptions.Select(e => (Concept: c, Exemption: e))).ToList();

        exemptions.Should().NotBeEmpty();
        foreach (var (concept, exemption) in exemptions)
        {
            var text = File.ReadAllText(Path.Combine(root, exemption.File));
            concept.Patterns.Should().Contain(pattern => pattern.IsMatch(text), $"{exemption.File} is exempt from {concept.Name} but uses none of it");
        }
    }

    [Theory]
    [InlineData("var rule = new AlertRuleEntity();", true)]
    [InlineData("/// <see cref=\"AlertRuleEntity.AllowThroughDnd\"/>", true)]
    [InlineData("var rule = new AlertRuleEntityBuilder();", false)]
    [InlineData("await db.AlertRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);", true)]
    [InlineData("var rules = alerts.AlertRulesController;", false)]
    [InlineData("var rules = await _alerts.GetRulesAsync(tenant, ct); // AlertRules", false)]
    public void FindsAWholeNameOrAMemberAccessOnly(string text, bool expected) =>
        Concepts[0].Patterns.Any(pattern => pattern.IsMatch(text)).Should().Be(expected);

    private static Regex NamePattern(string entity) => new($@"\b{Regex.Escape(entity)}\b", RegexOptions.CultureInvariant);

    private static Regex DbSetPattern(string dbSet) => new($@"\.{Regex.Escape(dbSet)}\b", RegexOptions.CultureInvariant);

    /// <summary>The entity names a type names, directly or as a generic or element type.</summary>
    private static IEnumerable<string> EntitiesIn(Type type)
    {
        if (type.HasElementType)
            return EntitiesIn(type.GetElementType()!);

        if (type.IsGenericType)
            return type.GetGenericArguments().SelectMany(EntitiesIn);

        return type.Assembly == Data ? [type.Name] : [];
    }

    private static IEnumerable<string> HostFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, Host), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}
