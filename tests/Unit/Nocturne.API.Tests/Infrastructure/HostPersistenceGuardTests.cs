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
/// navigation is not caught at all: a subject read as <c>TenantMember.Subject</c> is a membership
/// read, and a tenant read as <c>MemberInvite.Tenant</c> off a
/// table outside every concept is not seen either. An <see cref="Exemption"/> names a file that may still use the
/// concept's <c>DbSet</c>s or types, with the reason; a later slice or follow-up deletes it. One
/// that <see cref="Exemption.CoversMembers"/> also exempts, for its own concept only, the members
/// of the type the file is named after and of the types nested in it, which is where a local the
/// file holds across an <c>await</c> ends up.
/// </remarks>
[Trait("Category", "Unit")]
public class HostPersistenceGuardTests
{
    private sealed record Exemption(string File, string Reason, bool CoversMembers = false);

    private sealed record Concept(string Name, string[] Entities, string[] DbSets, Exemption[] Exemptions)
    {
        public IEnumerable<Regex> Patterns =>
            Entities.Select(NamePattern).Concat(DbSets.Select(DbSetPattern));

        public bool Exempts(string path) => Exemptions.Any(e => e.File == path);

        public bool Exempts(Type type) => Exemptions.Any(e =>
            e.CoversMembers && Path.GetFileNameWithoutExtension(e.File) == Outermost(type).Name);

        private static Type Outermost(Type type) => type.DeclaringType is { } outer ? Outermost(outer) : type;
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
            []),
        new("grants and invites (#3102)",
            ["OAuthGrantEntity", "MemberInviteEntity", "RefreshTokenEntity"],
            ["OAuthGrants", "MemberInvites", "RefreshTokens"],
            [
                new("src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs",
                    "dev-only whole-schema snapshot restore that re-points the invites of a tenant alongside every other table in one transaction",
                    CoversMembers: true),
            ]),
        new("OAuth clients (#3102)",
            ["OAuthClientEntity"],
            ["OAuthClients"],
            [
                new("src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs",
                    "dev-only whole-schema snapshot export and restore of a tenant's OAuth clients alongside every other table",
                    CoversMembers: true),
            ]),
        new("subjects and credentials (#3102)",
            ["SubjectEntity", "PasskeyCredentialEntity", "RecoveryCodeEntity", "TotpCredentialEntity",
                "SubjectOidcIdentityEntity", "SubjectAvatarEntity", "SubjectRoleEntity"],
            ["Subjects", "PasskeyCredentials", "RecoveryCodes", "TotpCredentials", "SubjectOidcIdentities",
                "SubjectAvatars", "SubjectRoles"],
            [
                new("src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs",
                    "dev-only whole-schema snapshot export and restore of a tenant's subjects and passkeys alongside every other table",
                    CoversMembers: true),
                new("src/API/Nocturne.API/Services/Alerts/AlertReceiptService.cs",
                    "static helper over the alert read's own context that names the members who saw an alarm from their subject rows"),
            ]),
        new("tenants and memberships (#3102)",
            ["TenantEntity", "TenantMemberEntity", "TenantRoleEntity", "TenantMemberRoleEntity", "MembershipRequestEntity",
                "OidcProviderEntity", "RoleEntity", "TenantDemoConfigEntity"],
            // Roles is not listed: ".Roles" is also a member of AuthContext, the Nightscout Subject, the
            // token claims and TenantMemberDto, so the global role catalogue is guarded by its entity type
            // alone, and a bare "db.Roles" query that names no RoleEntity is the one shape the scan misses.
            ["Tenants", "TenantMembers", "TenantRoles", "TenantMemberRoles", "MembershipRequests", "OidcProviders", "TenantDemoConfigs"],
            [
                new("src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs",
                    "dev-only whole-schema snapshot export and restore of a tenant, its roles and memberships alongside every other table",
                    CoversMembers: true),
            ]),
        new("connector configuration (#3103)",
            ["ConnectorConfigurationEntity", "ConnectorResetJobEntity"],
            ["ConnectorConfigurations", "ConnectorResetJobs"],
            [
                new("src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs",
                    "dev-only whole-schema snapshot export and restore of a tenant's connector configurations alongside every other table",
                    CoversMembers: true),
            ]),
        new("data sources (#3103)",
            ["DataSourceMetadataEntity"],
            ["DataSourceMetadata"],
            []),
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
        var entities = Concepts.SelectMany(c => c.Entities.Select(entity => (entity, concept: c)))
            .ToDictionary(pair => pair.entity, pair => pair.concept, StringComparer.Ordinal);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var members = Api.GetTypes()
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Concat(t.GetFields(all).Select(f => f.FieldType))
                .Concat(t.GetProperties(all).Select(p => p.PropertyType))
                .SelectMany(EntitiesIn)
                .Where(entity => entities.TryGetValue(entity, out var concept) && !concept.Exempts(t))
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

    /// <summary>
    /// A member exemption is the listing concept's alone: the same file's types stay checked for
    /// every other concept, and a file exempt from the text scan only is not exempt here at all.
    /// </summary>
    [Fact]
    public void AMemberExemptionBelongsToTheConceptThatListsIt()
    {
        var devAdmin = Api.GetType("Nocturne.API.Controllers.V4.DevOnly.DevAdminController")!;
        var receipts = Api.GetType("Nocturne.API.Services.Alerts.AlertReceiptService")!;
        var grants = Concepts.Single(c => c.Name.StartsWith("grants", StringComparison.Ordinal));
        var tokens = Concepts.Single(c => c.Name.StartsWith("OAuth tokens", StringComparison.Ordinal));
        var subjects = Concepts.Single(c => c.Name.StartsWith("subjects", StringComparison.Ordinal));

        grants.Exempts(devAdmin).Should().BeTrue();
        grants.Exempts(devAdmin.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault() ?? devAdmin).Should().BeTrue();
        tokens.Exempts(devAdmin).Should().BeFalse("the file is listed by another concept");
        Concepts[0].Exempts(devAdmin).Should().BeFalse();
        subjects.Exempts("src/API/Nocturne.API/Services/Alerts/AlertReceiptService.cs").Should().BeTrue();
        subjects.Exempts(receipts).Should().BeFalse("a text-scan exemption does not cover members");
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
