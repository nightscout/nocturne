using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A first-party session is an access token of the session shape and the <c>refresh_tokens</c> row
/// that renews it. <see cref="ISessionService.IssueSessionAsync"/> makes both, and a person's sign-in
/// reaches it only through <c>SignInCompletion</c>, which runs admission, the second-factor gate,
/// last-login and the <see cref="AuthAuditEventType.Login"/> row around it. The members below are the
/// only others allowed to issue one, each with its reason. The other lists pin the code the handoff
/// redeems, who holds the store of rows, and every other way a session's row or token can be made, so
/// the first list cannot be stepped around. A new member is added deliberately or not at all.
/// </summary>
/// <remarks>
/// The scan is syntactic, so these are not seen: a <c>refresh_tokens</c> row added through a set or a
/// type the source does not name (<c>db.Add(new() { ... })</c>); the store's insert called, in a
/// member of a holder, on a name not declared with the store's type; an expiry moved in an object
/// initializer, or a revocation cleared with a value not written as <c>null</c> or <c>default</c>.
/// An allowlist key names a member, not an overload, so a new overload of a listed member shares its
/// entry.
/// </remarks>
[Trait("Category", "Unit")]
public partial class SessionIssueAllowlistTests
{
    private const string Api = "src/API/Nocturne.API/";
    private const string Repositories = "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/";

    private const string DevOnly =
        "dev-only: a .DevOnly controller is registered only when DevOnlyEndpoints.AreEnabled, in Development or "
        + "with NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS=true for the end-to-end suite";

    private const string DemoMember =
        "the demo tenant's shared demo member (DemoTenantService.FindDemoMemberSubjectIdAsync), only on a tenant "
        + "whose IsDemo flag is set";

    private static readonly AllowlistFence Issuers = new(
        "IssueSessionAsync starts a session; a person's sign-in goes through SignInCompletion",
        new Dictionary<string, string>
        {
            [Api + "Services/Auth/SignInCompletion.cs: SignInCompletion.CompleteAsync"] =
                "every sign-in a person makes, after the method's proof, admission and the second-factor gate",
            [Api + "Controllers/Authentication/LoginHandoffController.cs: LoginHandoffController.ExchangeLoginCode"] =
                "redeems a single-use Login code a platform admin minted for a member of this host's tenant "
                + "(TenantController.IssueLoginCode, the one mint LoginCodes pins), while the member is still a live "
                + "member; the session carries the code, so it reads as handed off and is not an interactive session",
            [Api + "Controllers/V4/Demo/DemoSessionController.cs: DemoSessionController.CreateSession"] =
                DemoMember + ", never on a share host",
            [Api + "Services/Docs/ScalarAuthProvider.cs: ScalarAuthProvider.GetDemoBearerTokenAsync"] =
                DemoMember + " that also allows public docs: the access token the API docs page sends",
            [Api + "Controllers/V4/DevOnly/DevAuthController.cs: DevAuthController.IssueSessionAsync"] = DevOnly,
            [Api + "Controllers/V4/DevOnly/DevAuthController.cs: DevAuthController.Login"] =
                DevOnly + "; calls the controller's own IssueSessionAsync",
            [Api + "Controllers/V4/DevOnly/DevAuthController.cs: DevAuthController.LoginJson"] =
                DevOnly + "; calls the controller's own IssueSessionAsync",
            [Api + "Controllers/V4/DevOnly/DevAdminController.cs: DevAdminController.SeedTenant"] = DevOnly,
        },
        source => SourceMentions.Calling(source, [nameof(ISessionService.IssueSessionAsync)]));

    /// <summary>
    /// The members that name <see cref="LoginCodePurpose.Login"/>: a code of that purpose is what the
    /// handoff redeems for a session, so its mint is the handoff's proof.
    /// </summary>
    /// <remarks>
    /// The purpose is the enum's zero value and the entity's default, so a code minted with
    /// <c>default</c>, an unset purpose or a purpose passed through from elsewhere is not seen; the
    /// one row writer, <c>LoginCodeService</c>, is pinned by <see cref="CredentialWriterGuardTests"/>.
    /// </remarks>
    private static readonly AllowlistFence LoginCodes = new(
        "a Login code is redeemed at the handoff for a session; only a platform admin mints one",
        new Dictionary<string, string>
        {
            [Api + "Controllers/V4/PlatformAdmin/TenantController.cs: TenantController.IssueLoginCode"] =
                "the mint: a platform admin (TenantController is platform_admin only), for an active member of the "
                + "tenant",
            [Api + "Controllers/Authentication/LoginHandoffController.cs: LoginHandoffController.ExchangeLoginCode"] =
                "the redeem, on the code's own tenant host, for a member still live there",
            [Api + "Services/Auth/LoginCodeService.cs: LoginCodeService.SupersededBy"] =
                "reads the purpose: a Login code voids no other code",
            [Api + "Services/Auth/LoginCodeService.cs: LoginCodeService.UsableByItsSubject"] =
                "reads the purpose: a Login code redeems only while its subject is active",
            [Repositories + "SubjectRepository.cs: SubjectRepository.SetupOwnerRefusedCodePurposes"] =
                "reads the purpose: a subject holding a live Login code is not adopted as the setup owner",
            ["src/Infrastructure/Nocturne.Infrastructure.Data/Entities/LoginCodeEntity.cs: LoginCodeEntity.Purpose"] =
                "the property's default; the one row writer, LoginCodeService.MintAsync, sets the purpose it is given",
        },
        source => SourceMentions.Reading(source, nameof(LoginCodePurpose), nameof(LoginCodePurpose.Login)));

    private const string TokenStore = nameof(IFirstPartyTokenRepository);

    /// <summary>
    /// The members that name the store of <c>refresh_tokens</c> rows. Its insert takes a record, which
    /// a target-typed <c>new(...)</c> or a <c>with</c> copy builds without naming the type, so who
    /// holds the store is pinned, and <see cref="RefreshRowWriters"/> reports its insert on any name a
    /// holder declares with the store's type. The implementation, which names the store only in its
    /// base list, is not a holder.
    /// </summary>
    private static readonly AllowlistFence TokenStoreHolders = new(
        "the refresh_tokens store is held only by the session services and the readers listed",
        new Dictionary<string, string>
        {
            ["src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs: "
                + "ServiceCollectionExtensions.AddNotificationAndEventRepositories"] = "registers the store",
            [Api + "Services/Auth/RefreshTokenService.cs: RefreshTokenService._repository"] = RefreshService,
            [Api + "Services/Auth/RefreshTokenService.cs: RefreshTokenService.RefreshTokenService"] = RefreshService,
            [Api + "Services/Demo/DemoTenantService.cs: DemoTenantService._sessions"] = DemoReset,
            [Api + "Services/Demo/DemoTenantService.cs: DemoTenantService.DemoTenantService"] = DemoReset,
            [Api + "Hubs/HubCredentialVerifier.cs: HubCredentialVerifier.HasSessionAsync"] =
                "reads only: whether a hub login's session still has a live row (HasLiveSessionAsync)",
            [Api + "Services/Auth/CallerSessionLiveness.cs: CallerSessionLiveness._sessions"] = MintLiveness,
            [Api + "Services/Auth/CallerSessionLiveness.cs: CallerSessionLiveness.CallerSessionLiveness"] = MintLiveness,
        },
        source => SourceMentions.Naming(source, [TokenStore]));

    private const string RefreshService =
        "RefreshTokenService: issues and rotates rows for SessionService, and revokes them";

    private const string MintLiveness =
        "reads only: whether a session caller's session still has a live row before an action mints (HasLiveSessionAsync)";

    private const string DemoReset = "deletes only: the rows of a demo member it retires (RetireDemoMemberAsync)";

    private const string SessionIssue = "the row of the session IssueSessionAsync issues";
    private const string Rotation =
        "rotation: the successor of a live, unexpired row of the same session, for an active subject, written in "
        + "the transaction that revokes the row it replaces; it continues a session and starts none";

    /// <summary>
    /// The members that write a <c>refresh_tokens</c> row: the service and repository calls that store
    /// one, the record and entity they are built from, the set they are added to, SQL that inserts
    /// one, and a write that moves a row's expiry or clears its revocation, which extends or revives a
    /// session.
    /// </summary>
    private static readonly AllowlistFence RefreshRowWriters = new(
        "a refresh_tokens row is a session; it is written only by IssueSessionAsync and by rotation, and only "
        + "revoked after",
        new Dictionary<string, string>
        {
            [Api + "Services/Auth/SessionService.cs: SessionService.IssueSessionAsync"] = SessionIssue,
            [Api + "Services/Auth/RefreshTokenService.cs: RefreshTokenService.CreateRefreshTokenAsync"] = SessionIssue,
            [Api + "Services/Auth/SessionService.cs: SessionService.RotateSessionAsync"] = Rotation,
            [Api + "Services/Auth/RefreshTokenService.cs: RefreshTokenService.RotateRefreshTokenAsync"] = Rotation,
            [Repositories + "EfFirstPartyTokenRepository.cs: EfFirstPartyTokenRepository.AddAsync"] =
                "the one place a row is added, for CreateAsync and TryRotateAsync",
        },
        source => SourceMentions.Calling(source, RefreshRowCalls)
            .Concat(SourceMentions.Constructing(source, ["RefreshTokenRecord", "RefreshTokenEntity"]))
            .Concat(StoreInserts(source))
            .Concat(!MentionsRefreshRows(source) ? [] : SourceMentions.Where(source, node => node switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: var write, Expression: var set } =>
                    SetWrites.Contains(write) && IsRefreshTokenSet(set),
                InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetProperty" },
                    ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax { Body: ExpressionSyntax property } }, var value],
                } => ExtendsOrRevives(property, value.Expression),
                AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax property, Right: var value } =>
                    ExtendsOrRevives(property, value),
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax property, Right: var value, Parent.Parent: WithExpressionSyntax } =>
                    ExtendsOrRevives(property, value),
                _ => false,
            }))
            .Concat(SqlInserts(source)));

    private static readonly string[] RefreshRowCalls =
    [
        nameof(IRefreshTokenService.CreateRefreshTokenAsync),
        nameof(IRefreshTokenService.RotateRefreshTokenAsync),
        nameof(IFirstPartyTokenRepository.TryRotateAsync),
    ];

    private static readonly string[] SetWrites = ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "Update"];

    private static bool MentionsRefreshRows(SourceFile source) =>
        source.Text.Contains("RefreshTokens", StringComparison.Ordinal)
        || source.Text.Contains("RefreshTokenEntity", StringComparison.Ordinal)
        || source.Text.Contains("RefreshTokenRecord", StringComparison.Ordinal);

    private static bool IsRefreshTokenSet(ExpressionSyntax set) => set switch
    {
        MemberAccessExpressionSyntax or IdentifierNameSyntax => SourceMentions.Rightmost(set) == "RefreshTokens",
        InvocationExpressionSyntax { Expression: var invoked } =>
            (invoked is MemberAccessExpressionSyntax { Name: var name } ? name : invoked) is GenericNameSyntax
            {
                Identifier.ValueText: "Set",
                TypeArgumentList.Arguments: [var entity],
            } && LastName(entity) == "RefreshTokenEntity",
        _ => false,
    };

    /// <summary>
    /// A write that moves an expiry, or sets a revocation to nothing. A revoke sets <c>RevokedAt</c>
    /// to a time, which this does not count.
    /// </summary>
    private static bool ExtendsOrRevives(ExpressionSyntax property, ExpressionSyntax value) =>
        SourceMentions.Rightmost(property) switch
        {
            "ExpiresAt" => true,
            "RevokedAt" => IsNothing(value),
            _ => false,
        };

    private static bool IsNothing(ExpressionSyntax value) => value switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsNothing(parenthesized.Expression),
        CastExpressionSyntax cast => IsNothing(cast.Expression),
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression)
            || literal.IsKind(SyntaxKind.DefaultLiteralExpression),
        DefaultExpressionSyntax => true,
        _ => false,
    };

    /// <summary>
    /// The members that call the store's insert on a name the source declares with the store's type,
    /// or on a store a service provider resolves in place.
    /// </summary>
    private static IEnumerable<string> StoreInserts(SourceFile source)
    {
        if (!source.Text.Contains(TokenStore, StringComparison.Ordinal))
            return [];

        var held = source.Tree.GetRoot().DescendantNodes()
            .SelectMany(node => node switch
            {
                VariableDeclarationSyntax variables => variables.Variables
                    .Where(v => IsStore(variables.Type) || v.Initializer?.Value.ToString().Contains(TokenStore) == true)
                    .Select(v => v.Identifier.ValueText),
                ParameterSyntax { Type: { } type } parameter when IsStore(type) => [parameter.Identifier.ValueText],
                PropertyDeclarationSyntax property when IsStore(property.Type) => [property.Identifier.ValueText],
                _ => Enumerable.Empty<string>(),
            })
            .ToHashSet();

        return SourceMentions.Where(source, node => node switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(IFirstPartyTokenRepository.CreateAsync) } call =>
                IsHeld(call.Expression),
            MemberBindingExpressionSyntax { Name.Identifier.ValueText: nameof(IFirstPartyTokenRepository.CreateAsync) } binding =>
                binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional && IsHeld(conditional.Expression),
            _ => false,
        });

        bool IsHeld(ExpressionSyntax store) =>
            held.Contains(SourceMentions.Rightmost(store)) || store.ToString().Contains(TokenStore, StringComparison.Ordinal);
    }

    private static bool IsStore(TypeSyntax type) =>
        LastName(type is NullableTypeSyntax nullable ? nullable.ElementType : type) == TokenStore;

    private static string LastName(TypeSyntax type) => type.ToString().Split('.')[^1];

    private const string SessionShape =
        "session shape (IJwtService.GenerateAccessToken without scopes)";

    private const string GrantShape =
        "grant shape (with scopes), a credential of another kind than a session";

    /// <summary>
    /// The members that mint an access token. The three of the session shape issue or renew a
    /// <c>refresh_tokens</c> row; the rest carry scopes, so the session handlers do not take them.
    /// </summary>
    private static readonly AllowlistFence AccessTokenMinters = new(
        "an access token is minted only with a session row or from a grant",
        new Dictionary<string, string>
        {
            [Api + "Services/Auth/SessionService.cs: SessionService.IssueSessionAsync"] =
                SessionShape + ", beside the row it issues",
            [Api + "Services/Auth/SessionService.cs: SessionService.RotateSessionAsync"] =
                SessionShape + ", from a live row it rotates",
            [Api + "Services/Auth/OidcAuthService.cs: OidcAuthService.RefreshSessionAsync"] =
                SessionShape + ", from a live row it keeps when rotation is off, for an active subject",
            [Api + "Services/Identity/AuthorizationService.cs: AuthorizationService.GenerateJwtFromDirectGrantAsync"] =
                GrantShape + ": a live direct grant's token exchanged for a JWT with its scopes and grant id",
            [Api + "Services/Auth/OAuthTokenService.cs: OAuthTokenService.GenerateAccessTokenAsync"] =
                GrantShape + ": an OAuth grant's access token, with its grant id",
            [Api + "Controllers/V4/PlatformAdmin/PlatformAccessController.cs: PlatformAccessController.Access"] =
                GrantShape + ": a platform admin's tenant-pinned platform-access token, for a caller already "
                + "signed in as one",
            [Api + "Controllers/V4/Connectors/CareLinkConnectController.cs: CareLinkConnectController.DesktopToken"] =
                GrantShape + ": a ten-minute connector-scoped token for the signed-in caller's desktop CareLink connect",
        },
        source => SourceMentions.Calling(source, [nameof(IJwtService.GenerateAccessToken)]));

    private static readonly IReadOnlyDictionary<string, AllowlistFence> Fences =
        new Dictionary<string, AllowlistFence>
        {
            [nameof(Issuers)] = Issuers,
            [nameof(LoginCodes)] = LoginCodes,
            [nameof(TokenStoreHolders)] = TokenStoreHolders,
            [nameof(RefreshRowWriters)] = RefreshRowWriters,
            [nameof(AccessTokenMinters)] = AccessTokenMinters,
        };

    public static TheoryData<string> FenceNames => new(Fences.Keys);

    [Theory]
    [MemberData(nameof(FenceNames))]
    public void OnlyTheAllowlistDoesIt(string fence)
    {
        Fences[fence].Unlisted(RepoSources.Src).Should().BeEmpty(
            $"{Fences[fence].Rule}; allowlist a new member in {fence} deliberately, with its reason");
    }

    [Theory]
    [MemberData(nameof(FenceNames))]
    public void EveryAllowlistEntryStillDoesIt(string fence)
    {
        Fences[fence].Stale(RepoSources.Src).Should().BeEmpty(
            "a stale entry would silently cover a future call in that member");
    }

    [Theory]
    [InlineData(nameof(Issuers), "class Elsewhere { Task M(ISessionService s) => s.IssueSessionAsync(id, new SessionContext()); }")]
    [InlineData(nameof(LoginCodes), "class Elsewhere { Task M(ILoginCodeService c) => c.IssueAsync(db, s, LoginCodePurpose.Login, ct); }")]
    [InlineData(nameof(TokenStoreHolders), "class Elsewhere { Task M(IServiceProvider p) => p.GetRequiredService<IFirstPartyTokenRepository>().HasLiveSessionAsync(s, null, t, ct); }")]
    [InlineData(nameof(RefreshRowWriters), "class Elsewhere { Task M(IFirstPartyTokenRepository r, RefreshTokenRecord old) => r.CreateAsync(old with { Id = id }); }")]
    [InlineData(nameof(AccessTokenMinters), "class Elsewhere { string M(IJwtService j) => j.GenerateAccessToken(s, [], []); }")]
    public void AnUnlistedMemberIsReported(string fence, string source)
    {
        var elsewhere = new SourceFile(Api + "Controllers/V4/Elsewhere.cs", source);

        Fences[fence].Unlisted([elsewhere]).Should().Equal(Api + "Controllers/V4/Elsewhere.cs: Elsewhere.M");
    }

    [Theory]
    [InlineData(
        nameof(Issuers), "Controllers/Authentication/LoginHandoffController.cs", "LoginHandoffController.ExchangeLoginCode",
        "class LoginHandoffController { Task ExchangeLoginCode() => Task.CompletedTask; }",
        "class LoginHandoffController { Task ExchangeLoginCode() => _sessionService.IssueSessionAsync(id, c); }")]
    [InlineData(
        nameof(LoginCodes), "Controllers/V4/PlatformAdmin/TenantController.cs", "TenantController.IssueLoginCode",
        "class TenantController { Task IssueLoginCode() => IssueCodeAsync(id, s, LoginCodePurpose.OperatorRecovery); }",
        "class TenantController { Task IssueLoginCode() => IssueCodeAsync(id, s, LoginCodePurpose.Login); }")]
    [InlineData(
        nameof(TokenStoreHolders), "Hubs/HubCredentialVerifier.cs", "HubCredentialVerifier.HasSessionAsync",
        "class HubCredentialVerifier { Task<bool> HasSessionAsync() => Task.FromResult(true); }",
        "class HubCredentialVerifier { Task<bool> HasSessionAsync() => services.GetRequiredService<IFirstPartyTokenRepository>().HasLiveSessionAsync(s, i, n, ct); }")]
    [InlineData(
        nameof(RefreshRowWriters), "Services/Auth/RefreshTokenService.cs", "RefreshTokenService.CreateRefreshTokenAsync",
        "class RefreshTokenService { IFirstPartyTokenRepository _repository; Task CreateRefreshTokenAsync() => _repository.HasLiveSessionAsync(s, i, n, ct); }",
        "class RefreshTokenService { IFirstPartyTokenRepository _repository; Task CreateRefreshTokenAsync(RefreshTokenRecord r) => _repository.CreateAsync(r); }")]
    public void AnEntryWhoseMemberNoLongerDoesItIsStale(string fence, string file, string member, string without, string with)
    {
        var entry = $"{Api}{file}: {member}";

        Fences[fence].Stale([new SourceFile(Api + file, without)]).Should().Contain(entry);
        Fences[fence].Stale([new SourceFile(Api + file, with)]).Should().NotContain(entry);
    }

    [Theory]
    [InlineData("class C { Task M(ISessionService s) => s.IssueSessionAsync(id, c); }", "C.M")]
    [InlineData("class C { Task M(SessionService s) => s.IssueSessionAsync(id, c); }", "C.M")]
    [InlineData("class C { Task? M(ISessionService? s) => s?.IssueSessionAsync(id, c); }", "C.M")]
    [InlineData("class C { void M(ISessionService s) => Use(s.IssueSessionAsync); }", "C.M")]
    [InlineData("class C { Func<Task> F = () => s.IssueSessionAsync(id, c); }", "C.F")]
    [InlineData("class O { class C { C(ISessionService s) => s.IssueSessionAsync(id, c); } }", "O.C.C")]
    [InlineData("class C { Task M(ISessionService s) => s.RotateSessionAsync(t, c); }", null)]
    public void FindsTheIssuingMember(string source, string? expected) =>
        Found(Issuers, source).Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    [Theory]
    [InlineData("class C { Task M(IRefreshTokenService r) => r.CreateRefreshTokenAsync(id); }", "C.M")]
    [InlineData("class C { Task M(IFirstPartyTokenRepository r) => r.TryRotateAsync(id, s); }", "C.M")]
    [InlineData("class C { object M() => new RefreshTokenRecord(id, h, s, null, null, null, t, t, null, null, null, null); }", "C.M")]
    [InlineData("class C { object M() => new Nocturne.Infrastructure.Data.Entities.RefreshTokenEntity { Id = id }; }", "C.M")]
    [InlineData("class C { void M(NocturneDbContext db) => db.RefreshTokens.Add(e); }", "C.M")]
    [InlineData("class C { void M(SubjectEntity s) => s.RefreshTokens.AddRange(e); }", "C.M")]
    [InlineData("class C { const string Sql = \"INSERT INTO refresh_tokens (id) VALUES (@id)\"; }", "C.Sql")]
    [InlineData("class C { string M() => $\"insert into public.\\\"refresh_tokens\\\" (id) values ({id})\"; }", "C.M")]
    [InlineData("class C { bool M(NocturneDbContext db) => db.RefreshTokens.Any(t => t.SubjectId == id); }", null)]
    [InlineData("class C { void M(NocturneDbContext db) => db.OAuthRefreshTokens.Add(new OAuthRefreshTokenEntity()); }", null)]
    [InlineData("class C { const string Sql = \"INSERT INTO oauth_refresh_tokens (id) VALUES (@id)\"; }", null)]
    [InlineData("class C { IFirstPartyTokenRepository _r; Task M() => _r.CreateAsync(new(id, h, s)); }", "C.M")]
    [InlineData("class C { IFirstPartyTokenRepository? R { get; } Task M() => this.R!.CreateAsync(r); }", "C.M")]
    [InlineData("class C { IFirstPartyTokenRepository? _r; Task? M() => _r?.CreateAsync(r); }", "C.M")]
    [InlineData("class C { Task M(IServiceProvider p) { var r = p.GetRequiredService<IFirstPartyTokenRepository>(); return r.CreateAsync(x); } }", "C.M")]
    [InlineData("class C { Task M(IServiceProvider p) => p.GetRequiredService<IFirstPartyTokenRepository>().CreateAsync(x); }", "C.M")]
    [InlineData("class C { IFirstPartyTokenRepository _r; Task M(ITenantRepository t) => t.CreateAsync(x); }", null)]
    [InlineData("class C { Task M(ITenantRepository t) => t.CreateAsync(x); }", null)]
    [InlineData("class C { Task M(NocturneDbContext db) => db.Set<RefreshTokenEntity>().AddAsync(e); }", "C.M")]
    [InlineData("class C { Task M(NocturneDbContext db) => db.Set<OAuthRefreshTokenEntity>().AddAsync(e); }", null)]
    [InlineData("class C { Task M(NocturneDbContext db) => db.RefreshTokens.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, later)); }", "C.M")]
    [InlineData("class C { Task M(NocturneDbContext db) => db.RefreshTokens.ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, (DateTime?)null)); }", "C.M")]
    [InlineData("class C { Task M(NocturneDbContext db) => db.RefreshTokens.ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now)); }", null)]
    [InlineData("class C { void M(RefreshTokenEntity e) => e.RevokedAt = null; }", "C.M")]
    [InlineData("class C { void M(RefreshTokenEntity e) => e.RevokedAt = DateTime.UtcNow; }", null)]
    [InlineData("class C { void M(RefreshTokenEntity e) => e.ExpiresAt = later; }", "C.M")]
    [InlineData("class C { object M(RefreshTokenRecord r) => r with { ExpiresAt = later }; }", "C.M")]
    [InlineData("class C { object M(RefreshTokenRecord r) => r with { RevokedAt = default }; }", "C.M")]
    [InlineData("class C { void M(LoginCodeEntity c) => c.ExpiresAt = later; }", null)]
    public void FindsTheMemberWritingARefreshRow(string source, string? expected) =>
        Found(RefreshRowWriters, source).Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    [Theory]
    [InlineData("class C { string M(IJwtService j) => j.GenerateAccessToken(s, [], []); }", "C.M")]
    [InlineData("class C { string M(IJwtService j) => j.GenerateAccessToken(s, [], [], scopes: [\"x\"]); }", "C.M")]
    [InlineData("class C { bool M(IJwtService j) => j.ValidateAccessToken(t).IsValid; }", null)]
    public void FindsTheMemberMintingAnAccessToken(string source, string? expected) =>
        Found(AccessTokenMinters, source).Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    [Theory]
    [InlineData("class C { Task M(ILoginCodeService c) => c.IssueAsync(db, s, LoginCodePurpose.Login, ct); }", "C.M")]
    [InlineData("class C { LoginCodePurpose[] P = [Nocturne.Infrastructure.Data.Entities.LoginCodePurpose.Login]; }", "C.P")]
    [InlineData("class C { bool M(LoginCodeEntity c) => c.Purpose == LoginCodePurpose.Login; }", "C.M")]
    [InlineData("class C { Task M(ILoginCodeService c) => c.IssueAsync(db, s, LoginCodePurpose.Recovery, ct); }", null)]
    [InlineData("class C { Task M(IAuthAuditService a) => a.LogAsync(AuthAuditEventType.Login, s, true); }", null)]
    public void FindsTheMemberNamingALoginCode(string source, string? expected) =>
        Found(LoginCodes, source).Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    [Theory]
    [InlineData("class C { private readonly IFirstPartyTokenRepository _r; }", "C._r")]
    [InlineData("class C { C(IFirstPartyTokenRepository r) { } }", "C.C")]
    [InlineData("class C { object M(IServiceProvider p) => p.GetRequiredService<IFirstPartyTokenRepository>(); }", "C.M")]
    [InlineData("class C : IFirstPartyTokenRepository { }", null)]
    [InlineData("class C { C(IOAuthTokenRepository r) { } }", null)]
    public void FindsTheMemberHoldingTheStore(string source, string? expected) =>
        Found(TokenStoreHolders, source).Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    private static IReadOnlyList<string> Found(AllowlistFence fence, string source) =>
        fence.FoundIn([new SourceFile("src/X.cs", source)]);

    private static IEnumerable<string> SqlInserts(SourceFile source) =>
        source.Text.Contains("refresh_tokens", StringComparison.OrdinalIgnoreCase)
            ? SqlLiterals.In(source.Tree)
                .Where(literal => InsertIntoRefreshTokens().IsMatch(literal.Text))
                .Select(literal => $"{source.Path}: {EnclosingMember.Of(literal.Node)}")
                .Distinct()
            : [];

    [GeneratedRegex("""insert\s+into\s+(?:"?public"?\.)?"?refresh_tokens\b""", RegexOptions.IgnoreCase)]
    private static partial Regex InsertIntoRefreshTokens();
}
