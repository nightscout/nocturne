using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nocturne.API.Extensions;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A concept whose persistence module is in Infrastructure.Data is reached from the web host
/// through its contract only: no file in the API project names one of its entity types, and no
/// member of the API assembly takes or returns one. Each slice of #2129 adds its concept to
/// <see cref="Enforced"/> as it lands; a concept in <see cref="Pending"/> is listed so the next
/// slice has its entity set to hand and the names cannot go stale meanwhile.
/// </summary>
/// <remarks>
/// A file is named in <see cref="Concept.ExemptFiles"/> only for a port still under way, with the
/// slice that removes the exemption. The text scan counts a name anywhere in the file, doc
/// comments included: an entity the host still talks about is one it still reaches for.
/// </remarks>
[Trait("Category", "Unit")]
public class HostPersistenceGuardTests
{
    private sealed record Concept(string Name, string[] Entities, string[] ExemptFiles);

    private static readonly Concept[] Enforced =
    [
        new("alert rules (#3100)", ["AlertRuleEntity", "AlertRuleChannelEntity"], []),
    ];

    private static readonly Concept[] Pending =
    [
        new("OAuth grants and tokens (#3101)",
            ["OAuthClientEntity", "OAuthGrantEntity", "OAuthAuthorizationCodeEntity", "OAuthRefreshTokenEntity", "OAuthDeviceCodeEntity", "RefreshTokenEntity"],
            []),
        new("identity and tenancy (#3102)",
            ["SubjectEntity", "SubjectRoleEntity", "SubjectOidcIdentityEntity", "TenantEntity", "TenantMemberEntity", "TenantMemberRoleEntity", "TenantRoleEntity"],
            []),
        new("connector configuration (#3103)", ["ConnectorConfigurationEntity"], []),
    ];

    private const string Host = "src/API/Nocturne.API";

    private static readonly Assembly Api = typeof(ServiceRegistrationExtensions).Assembly;
    private static readonly Assembly Data = typeof(NocturneDbContext).Assembly;

    [Fact]
    public void NoHostFileNamesAnEnforcedEntity()
    {
        var root = RepositoryRoot();
        var namers = HostFiles(root)
            .SelectMany(path => Enforced
                .Where(concept => !concept.ExemptFiles.Contains(path))
                .SelectMany(concept => concept.Entities)
                .Where(entity => NamePattern(entity).IsMatch(File.ReadAllText(Path.Combine(root, path))))
                .Select(entity => $"{path}({entity})"))
            .ToList();

        namers.Should().BeEmpty("the host reaches a ported concept's rows through its repository contract, not its entities");
    }

    [Fact]
    public void NoHostMemberTakesOrReturnsAnEnforcedEntity()
    {
        var entities = Enforced.SelectMany(c => c.Entities).ToHashSet(StringComparer.Ordinal);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var members = Api.GetTypes()
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
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
        var listed = Enforced.Concat(Pending).SelectMany(c => c.Entities).Distinct();

        listed.Should().OnlyContain(entity => declared.Contains(entity), "a renamed entity would drop out of the guard");
    }

    [Fact]
    public void EveryExemptFileExistsAndNamesItsConcept()
    {
        var root = RepositoryRoot();
        foreach (var concept in Enforced)
        {
            foreach (var path in concept.ExemptFiles)
            {
                var text = File.ReadAllText(Path.Combine(root, path));
                concept.Entities.Should().Contain(entity => NamePattern(entity).IsMatch(text), $"{path} is exempt from {concept.Name} but names none of its entities");
            }
        }
    }

    [Theory]
    [InlineData("var rule = new AlertRuleEntity();", true)]
    [InlineData("/// <see cref=\"AlertRuleEntity.AllowThroughDnd\"/>", true)]
    [InlineData("var rule = new AlertRuleEntityBuilder();", false)]
    [InlineData("var rules = alerts.AlertRules;", false)]
    public void FindsAWholeNameOnly(string text, bool expected) =>
        NamePattern("AlertRuleEntity").IsMatch(text).Should().Be(expected);

    private static Regex NamePattern(string entity) => new($@"\b{Regex.Escape(entity)}\b", RegexOptions.CultureInvariant);

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
