using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Services;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Multitenancy;
using Nocturne.API.RateLimiting;
using Nocturne.API.Services.AidDetection;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.CoachMarks;
using Nocturne.Core.Contracts.Content;
using Nocturne.Core.Contracts.Translations;
using Nocturne.API.Services.Timezones;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Docs;
using Nocturne.API.Services.Effects;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Sleep;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.NotificationTemplates;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Monitoring;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Net;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using JwtOptions = Nocturne.Core.Models.Configuration.JwtOptions;
using OidcOptions = Nocturne.Core.Models.Configuration.OidcOptions;
using OpenTelemetry.Metrics;

namespace Nocturne.API.Extensions;

/// <summary>
/// Extension methods that organize DI registrations into logical groups,
/// keeping Program.cs scannable.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Rate-limiting policy for the documentation endpoints, which are mapped rather than
    /// controller actions and so cannot carry the attribute.
    /// </summary>
    public const string DocsRateLimitPolicy = "docs";

    /// <summary>
    /// Rate-limiting policy for connector credential verification, which is named here rather than
    /// inline so the action's attribute and the registration read the same value.
    /// </summary>
    public const string ConnectorVerifyRateLimitPolicy = "connector-verify";

    /// <summary>
    /// The rate-limiting policies partitioned on the calling client, with the ceiling and window
    /// each applies. Held as one table so all of them resolve their partition through
    /// <see cref="ClientRateLimitKey"/> and the trust decision behind a forwarded address is taken
    /// in exactly one place.
    /// </summary>
    internal static readonly (string Policy, int PermitLimit, TimeSpan Window)[] ClientAddressPolicies =
    [
        ("oauth-token", 30, TimeSpan.FromMinutes(1)),
        ("oauth-device", 10, TimeSpan.FromMinutes(1)),
        // RFC 7591 Dynamic Client Registration.
        ("oauth-register", 10, TimeSpan.FromHours(1)),
        ("oauth-device-approve", 20, TimeSpan.FromMinutes(1)),
        ("totp-login", 10, TimeSpan.FromMinutes(1)),
        // The passkey ceremonies. An assertion is phishing-resistant and its challenge is a
        // stateless Data Protection token, so what these bound is the work each attempt costs —
        // the credential lookup, the audit row a failure writes, and the crypto the completion
        // step runs. Two things set the ceilings well above that work: a ceremony spends two
        // permits (options then complete), and a household or clinic behind one NAT is a single
        // partition, so the whole family signs in from one bucket.
        ("passkey-login", 30, TimeSpan.FromMinutes(1)),
        ("passkey-register", 20, TimeSpan.FromMinutes(1)),
        // A recovery code is a one-time human-typed secret and the last way back into an account,
        // so the ceiling has to leave room to mistype ten characters under stress. The window,
        // not the ceiling, is what makes this an order of magnitude tighter than totp-login: an
        // authenticator code rotates every 30 seconds, a recovery code does not.
        ("passkey-recovery", 10, TimeSpan.FromMinutes(10)),
        // The one anonymous ceremony that writes a row before any credential exists: each start
        // files a pending subject under a display name not already taken, and nothing else prunes
        // them.
        ("passkey-access-request", 5, TimeSpan.FromMinutes(10)),
        // First-run setup: creating the tenant, the owner ceremonies (options then complete, so two
        // permits each) and the OIDC callback that exchanges a code with the provider. Only
        // reachable while no member of the instance holds a credential, so the ceiling covers one
        // operator retrying a ceremony rather than a population signing in.
        ("setup", 20, TimeSpan.FromMinutes(1)),
        // The "is this name free?" probes — owner username and tenant slug — which a form issues
        // per keystroke behind a 400ms debounce, so a hunt-and-peck typist spends a permit per
        // character. Sized for a full name typed that way plus a retry; past that the frontend
        // carries it, treating a probe it could not complete as unverified rather than refused.
        // The ceiling bounds what each anonymous request costs: a membership or tenant lookup,
        // and for the username the operator's optional validation webhook.
        ("name-availability", 60, TimeSpan.FromMinutes(1)),
        // The anonymous invite lookups, member and alert. Their tokens are long random strings, so
        // grinding one is infeasible at any rate; what the ceiling bounds is the database query
        // each anonymous request costs. An invite page reads once per visit, which leaves the
        // ceiling room for reloads and for a clinic behind one NAT.
        ("invite-lookup", 30, TimeSpan.FromMinutes(1)),
        ("guest-activate", 5, TimeSpan.FromMinutes(10)),
        // Redeeming a login code. The code is a random string of refresh-token length, so grinding
        // one is infeasible at any rate; the ceiling bounds what an anonymous attempt costs, which
        // is one indexed lookup and the audit row a refusal writes. A browser handed a code spends
        // one permit, and every code its holder could legitimately present was minted in the last
        // five minutes.
        ("login-handoff", 10, TimeSpan.FromMinutes(1)),
        // Friction against naive abuse only — this does NOT bound the refresh_tokens table. The
        // real ceiling is DemoSessionLimits.MaxLiveSessions, enforced on the subject id.
        ("demo-session", 10, TimeSpan.FromMinutes(5)),
        ("support-issues", 5, TimeSpan.FromHours(1)),
        // Each contribution opens an upstream PR, directly or through the relay, so the ceiling
        // bounds how much of that a caller can spend. The page is server-rendered, so the address
        // only distinguishes contributors when it comes off the signed header. One bucket for
        // every contribution flow: what is bounded is pull requests upstream, not endpoints.
        (ContributionsRateLimitPolicy, 10, TimeSpan.FromHours(1)),
        // Connector credential verification drives a live sign-in against the external provider
        // from this deployment's address, so the ceiling bounds both provider-side lockouts and
        // use of the API as a credential-testing proxy.
        (ConnectorVerifyRateLimitPolicy, 5, TimeSpan.FromMinutes(5)),
        // The documentation surface (/scalar, /openapi) runs before tenant resolution and
        // authentication, and the reference reads the tenants table and may write that tenant's
        // OAuth client, so it is the one unauthenticated path that reaches the database that
        // early. What bounds the damage is elsewhere: the row holds at most
        // ScalarAuthProvider.MaxRedirectUris entries, and both the tenant resolution and the
        // client id are cached, so a flood mostly costs the page render.
        (DocsRateLimitPolicy, 30, TimeSpan.FromMinutes(1)),
    ];

    /// <summary>
    /// Rate-limiting policy for the statistics actions that compute over a caller-supplied body.
    /// Named here rather than inline so the guard test asserting every one of them carries it
    /// reads the same value the registration does.
    /// </summary>
    public const string StatisticsComputeRateLimitPolicy = "statistics-compute";

    /// <summary>
    /// Partition key for <see cref="StatisticsComputeRateLimitPolicy"/>: the request host, lowered.
    /// </summary>
    /// <remarks>
    /// A host is case-insensitive and tenant resolution lower-cases the subdomain it reads, so a
    /// caller sending the same host in another casing reaches the same tenant. Keying on the raw
    /// string would hand them a fresh window per variant, and the caller this policy bounds — an
    /// anonymous share-link holder — writes the header themselves.
    /// </remarks>
    internal static string StatisticsComputePartitionKey(HttpContext context) =>
        context.Request.Host.Host.ToLowerInvariant();

    /// <summary>
    /// Rate-limiting policy for the per-session translation draft store.
    /// </summary>
    public const string TranslationDraftsRateLimitPolicy = "translation-drafts";

    /// <summary>
    /// Rate-limiting policy shared by every contribution flow that opens an
    /// upstream pull request (translations, CMS content). One bucket on
    /// purpose: the cost being bounded is PRs on the upstream repository, not
    /// requests to any one endpoint.
    /// </summary>
    public const string ContributionsRateLimitPolicy = "contributions";

    /// <summary>Shared bucket for draft requests that present no credential.</summary>
    internal const string AnonymousDraftPartition = "anonymous";

    /// <summary>
    /// Partition key for the translation-drafts limiter: the hashed credential
    /// the request presents. Not the IP — <c>UseForwardedHeaders</c> takes
    /// <c>RemoteIpAddress</c> from X-Forwarded-For with no trusted-proxy list,
    /// so the sibling per-IP policies are the wrong model to copy here.
    /// Hashing keeps no token as a dictionary key. Requests with no credential
    /// share one fixed bucket, so an anonymous flood cannot evict an editor's.
    /// Channel precedence follows the handler chain in
    /// <c>AuthenticationMiddleware</c> and is pinned by
    /// <c>TranslationDraftPartitionKeyTests</c>. Two residual bypasses remain,
    /// each needing a platform-admin or api-secret credential to reach;
    /// closing them needs partitioning after authentication.
    /// </summary>
    internal static string TranslationDraftPartitionKey(HttpContext context)
    {
        var cookie = context.RequestServices.GetRequiredService<IOptions<OidcOptions>>().Value.Cookie;
        var credential =
            context.Request.Cookies[cookie.AccessTokenName]
            ?? context.Request.Cookies[cookie.RefreshTokenName]
            ?? TokenCredential(context.Request);

        return string.IsNullOrEmpty(credential)
            ? AnonymousDraftPartition
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    /// <summary>
    /// Reduces the Authorization header and <c>?token=</c> query parameter to
    /// the one token the handlers would authenticate on, collapsing the
    /// spellings they treat as one credential — hashing each separately would
    /// give one caller a 60/min allowance per variant.
    /// </summary>
    private static string? TokenCredential(HttpRequest request)
    {
        var header = request.Headers.Authorization.FirstOrDefault();

        if (!string.IsNullOrEmpty(header)
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = header["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(bearer))
            {
                return bearer;
            }
        }

        var queryToken = request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryToken))
        {
            return queryToken.StartsWith(DirectGrantTokenHandler.TokenPrefix, StringComparison.Ordinal)
                ? queryToken
                : DirectGrantTokenHandler.TokenPrefix + queryToken;
        }

        return string.IsNullOrEmpty(header) ? header : header.Trim();
    }

    /// <summary>
    /// Core API utility and calculation services (status, versioning, time queries,
    /// IOB/COB, predictions, statistics, etc.)
    /// </summary>
    public static IServiceCollection AddApiCoreServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // The clock every constructor-injected TimeProvider resolves to, stated here rather than
        // left to AddAuthentication, which TryAdds the same instance in passing. In this host
        // AddNocturneMemoryCache has already TryAdded it, so this is the registration for hosts
        // that do not add the cache.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IStatusService, StatusService>();
        services.AddScoped<IVersionService, VersionService>();
        services.AddSingleton<IXmlDocumentationService, XmlDocumentationService>();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();

        services.AddScoped<IBraceExpansionService, BraceExpansionService>();
        services.AddScoped<ITimeQueryService, TimeQueryService>();

        services.AddScoped<IDDataService, DDataService>();
        services.AddScoped<IPropertiesService, PropertiesService>();
        services.AddScoped<ISummaryService, SummaryService>();
        // Prediction service — configurable via Predictions:Source (None, DeviceStatus, OrefWasm)
        var predictionSource = PredictionOptions.ResolveSource(configuration);
        switch (predictionSource)
        {
            case PredictionSource.DeviceStatus:
                services.AddScoped<IPredictionService, DeviceStatusPredictionService>();
                break;
            case PredictionSource.OrefWasm:
                services.AddScoped<IPredictionService, PredictionService>();
                services.AddOrefService(options =>
                {
                    options.WasmPath = "oref.wasm";
                    options.Enabled = true;
                });
                break;
            case PredictionSource.None:
            default:
                break;
        }

        services.AddScoped<IIobCalculator, IobCalculator>();
        services.AddScoped<ICobCalculator, CobCalculator>();
        services.AddScoped<IAr2Service, Ar2Service>();
        services.AddScoped<IBolusWizardService, BolusWizardService>();

        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IHubTokenAuthorizer, HubTokenAuthorizer>();
        services.AddScoped<IAlexaService, AlexaService>();

        services.AddScoped<IStatisticsService, StatisticsService>();
        services.AddScoped<ISensorIntegrityService, SensorIntegrityService>();

        // Analytics
        services.Configure<AnalyticsConfiguration>(
            configuration.GetSection(AnalyticsConfiguration.SectionName)
        );
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<IConnectorHealthService, ConnectorHealthService>();

        // GitHub issue creation
        services.Configure<GitHubIssueOptions>(configuration.GetSection("GitHub"));
        services.AddSingleton<GitHubIssueService>();
        services.AddScoped<ISupportDiagnosticsService, SupportDiagnosticsService>();

        services.Configure<GitHubContributionOptions>(configuration.GetSection("GitHub"));
        services.AddSingleton<GitHubPrClient>();
        services.AddSingleton<ITranslationContributionService, GitHubTranslationService>();
        services.AddSingleton<IContentContributionService, GitHubContentService>();
        services.AddScoped<ITranslationDraftService, TranslationDraftService>();

        return services;
    }

    /// <summary>
    /// Authentication, authorization, identity providers, multitenancy,
    /// and auth middleware handlers.
    /// </summary>
    public static IServiceCollection AddAuthenticationAndIdentity(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Options
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.PostConfigure<JwtOptions>(options =>
        {
            if (string.IsNullOrEmpty(options.SecretKey))
            {
                options.SecretKey =
                    configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
                    ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
                    ?? throw new InvalidOperationException(
                        "JWT signing key could not be derived: instance key is not configured.");
            }
        });
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));
        // Auth services
        services.AddScoped<IAuthAuditService, AuthAuditService>();
        services.AddScoped<IDirectGrantService, DirectGrantService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<ILoginCodeService, LoginCodeService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddSingleton<IRotationSuccessorCache, RotationSuccessorCache>();
        services.AddScoped<IFirstPartyTokenRepository, EfFirstPartyTokenRepository>();
        services.AddScoped<ISubjectService, SubjectService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IOidcProviderService, OidcProviderService>();
        services.AddScoped<IOidcAuthService, OidcAuthService>();
        services.AddScoped<PlatformAdminBootstrapService>();

        // OAuth services
        services.AddScoped<IOAuthClientService, OAuthClientService>();
        services.AddSingleton<RedirectUriValidator>();
        services.AddScoped<IOAuthGrantService, OAuthGrantService>();
        services.AddScoped<IJwtCredentialValidator, JwtCredentialValidator>();
        services.AddScoped<IOAuthTokenService, OAuthTokenService>();
        services.AddScoped<IOAuthDeviceCodeService, OAuthDeviceCodeService>();
        services.AddScoped<IMemberInviteService, MemberInviteService>();
        services.AddScoped<IMembershipRequestService, MembershipRequestService>();
        services.AddScoped<IGuestLinkService, GuestLinkService>();
        services.AddSingleton<IOAuthTokenRevocationCache, OAuthTokenRevocationCache>();
        services.AddHostedService<OAuthCodeCleanupService>();

        services.AddHostedService<AuthorizationSeedService>();

        services.AddSingleton<GuestSessionCacheService>();
        services.AddSingleton<PublicAccessCacheService>();
        services.AddSingleton<ShareTokenCacheService>();
        // Same instance behind the seam, so the cache is shared rather than duplicated.
        services.AddSingleton<IShareTokenResolver>(sp => sp.GetRequiredService<ShareTokenCacheService>());
        services.AddSingleton<IShareTokenGenerator, ShareTokenGenerator>();
        services.AddScoped<IShareLinkService, ShareLinkService>();
        services.AddScoped<IShareAppearanceReader>(sp => sp.GetRequiredService<IShareLinkService>());
        // Singleton because its consumer runs at startup outside any request scope; it creates its
        // own scope per notification.
        services.AddSingleton<IShareLinkRotatedNotifier, ShareLinkRotatedNotifier>();

        // Passkey (WebAuthn/FIDO2) services
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddScoped<IRecoveryCodeService, RecoveryCodeService>();
        services.AddScoped<ITotpService, TotpService>();
        // Derive WebAuthn RP config from the base domain (single source of truth). Blank counts as
        // unset: it is what the shipped .env.example leaves behind, and an origin built from it is
        // one Fido2Configuration refuses to parse.
        var configuredBaseDomain = configuration[BaseDomainOptions.ConfigKey];
        var baseDomain = string.IsNullOrWhiteSpace(configuredBaseDomain)
            ? "localhost:1612"
            : configuredBaseDomain;
        var rpId = baseDomain.Split(':')[0]; // hostname without port
        var origin = $"https://{baseDomain}";
        services.AddFido2(options =>
        {
            options.ServerDomain = rpId;
            options.ServerName = "Nocturne";
            options.Origins = new HashSet<string> { origin };
        });

        // Base domain (used by tenant resolution, OIDC redirects, etc.)
        services.Configure<BaseDomainOptions>(opts =>
            opts.BaseDomain = configuration[BaseDomainOptions.ConfigKey] ?? ""
        );

        // Derive the session-, state-, and platform-access cookie Domain attributes from the base
        // domain, in one place, so every writer and deleter of those cookies agrees on their scope.
        services.PostConfigure<OidcOptions>(opts =>
            SessionCookieExtensions.ApplyCookieDomainDefaults(opts, baseDomain)
        );

        // Operator (SaaS platform policy)
        services.AddOptions<OperatorConfiguration>()
            .Bind(configuration.GetSection(OperatorConfiguration.SectionName))
            .Validate(config =>
            {
                if (config.Support.AccountBilling is { } ab)
                    return !string.IsNullOrWhiteSpace(ab.Url);
                return true;
            }, "Operator:Support:AccountBilling:Url is required when AccountBilling is configured")
            .Validate(config =>
            {
                if (config.Support.AccountPortal is { } portal)
                    return !string.IsNullOrWhiteSpace(portal.Url);
                return true;
            }, "Operator:Support:AccountPortal:Url is required when AccountPortal is configured");

        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ITenantOwnerResolver, TenantOwnerResolver>();
        services.AddScoped<ITenantMemberService, TenantMemberService>();
        services.AddScoped<ITenantRoleService, TenantRoleService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantOverviewService, TenantOverviewService>();
        services.AddScoped<IInstanceSetupState, InstanceSetupState>();
        services.AddScoped<DemoTenantService>();
        services.AddScoped<ScalarAuthProvider>();

        // Shared by InstanceKeyHandler (authentication) and TenantSetupMiddleware
        // (setup-gate bypass) so instance-key validation rules live in one place.
        services.AddSingleton<IInstanceKeyValidator, InstanceKeyValidator>();

        // Auth handlers (executed in priority order, lowest first)
        services.AddSingleton<IAuthHandler, PlatformAccessCookieHandler>(); // Priority 40
        services.AddSingleton<IAuthHandler, SessionCookieHandler>(); // Priority 50
        services.AddSingleton<IAuthHandler, GuestSessionHandler>(); // Priority 52
        services.AddSingleton<GuestSessionHandler>(); // For direct cookie-setting use
        services.AddSingleton<IAuthHandler, InstanceKeyHandler>(); // Priority 55
        services.AddSingleton<IAuthHandler, OidcTokenHandler>(); // Priority 100
        services.AddSingleton<IAuthHandler, OAuthAccessTokenHandler>(); // Priority 150
        services.AddSingleton<IAuthHandler, DirectGrantTokenHandler>(); // Priority 150
        services.AddSingleton<IAuthHandler, LegacyJwtHandler>(); // Priority 200
        services.AddSingleton<IAuthHandler, ApiKeyHandler>(); // Priority 400

        // OIDC provider discovery HTTP client. The issuer URL is tenant configuration, and the
        // unsaved-provider test button hands the caller the status of whatever it reached. Redirects
        // stay on — an issuer that redirects its discovery path is ordinary, and the pin applies to
        // every hop's connect anyway.
        services.AddHttpClient(
            "OidcProvider",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }
        ).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = new PinnedConnector(OutboundAddressPolicy.NotLinkLocal).ConnectAsync,
        });

        var clientRateLimitKey = new ClientRateLimitKey(configuration);

        services.AddRateLimiter(options =>
        {
            foreach (var (policy, permitLimit, window) in ClientAddressPolicies)
            {
                options.AddPolicy(
                    policy,
                    context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: clientRateLimitKey.Resolve(context),
                            factory: _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = permitLimit,
                                Window = window,
                                QueueLimit = 0,
                            }
                        )
                );
            }

            // Statistics compute POSTs: 60 per tenant host per minute. These actions compute over a
            // caller-supplied body rather than over stored data, and reports.read — the scope
            // gating them — is held by every public share link, so an anonymous viewer can post
            // them. The partition is the Host rather than the client because what this bounds is one
            // tenant's compute across all of its viewers, and the limiter runs before tenant
            // resolution while the tenant (or share token) is already the subdomain.
            options.AddPolicy(
                StatisticsComputeRateLimitPolicy,
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: StatisticsComputePartitionKey(context),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 60,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }
                    )
            );

            // Translation drafts: 60 per session per minute, sliding. Autosave
            // batches every 800ms while typing, so the ceiling has to clear
            // normal editing while still bounding the per-call database work an
            // editor session can force. Partitioned by credential rather than
            // IP because the caller controls X-Forwarded-For; see
            // TranslationDraftPartitionKey.
            options.AddPolicy(
                TranslationDraftsRateLimitPolicy,
                context =>
                    RateLimitPartition.GetSlidingWindowLimiter(
                        partitionKey: TranslationDraftPartitionKey(context),
                        factory: _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = 60,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 6,
                            QueueLimit = 0,
                        }
                    )
            );

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        error = "rate_limit_exceeded",
                        error_description = "Too many requests. Please try again later.",
                    },
                    ct
                );
            };
        });

        return services;
    }

    /// <summary>
    /// Domain CRUD services for entries, treatments, device status, profiles,
    /// food, activities, trackers, and all other data-owning services.
    /// </summary>
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        // Demo mode
        services.AddSingleton<IDemoModeService, DemoModeService>();

        services.AddScoped<IV4ToLegacyProjectionService, V4ToLegacyProjectionService>();

        // Collection effect descriptors (resolved by WriteSideEffectsService)
        services.AddSingleton<ICollectionEffectDescriptor, ProfileEffectDescriptor>();
        services.AddSingleton<ICollectionEffectDescriptor, DeviceStatusEffectDescriptor>();
        services.AddSingleton<ICollectionEffectDescriptor, FoodEffectDescriptor>();

        // Core domain services
        services.AddScoped<ITreatmentService, TreatmentService>();
        services.AddScoped<ITreatmentStore, Nocturne.API.Services.Treatments.TreatmentReadService>();
        services.AddScoped<ITreatmentCache, Nocturne.API.Services.Treatments.TreatmentCacheAdapter>();
        services.AddScoped<SignalRTreatmentEventSink>();
        services.AddScoped<IDataEventSink<Treatment>>(sp =>
        {
            var sinks = new List<IDataEventSink<Treatment>>
            {
                sp.GetRequiredService<SignalRTreatmentEventSink>(),
            };
            var writeBack = sp.GetService<NightscoutTreatmentWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);

            return new CompositeDataEventSink<Treatment>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Treatment>>>());
        });
        services.AddScoped<IWriteSideEffects, WriteSideEffectsService>();
        services.AddScoped<IEntryService, EntryService>();
        services.AddScoped<IEntryStore, Nocturne.API.Services.Entries.EntryReadService>();
        services.AddScoped<IEntryCache, Nocturne.API.Services.Entries.EntryCacheAdapter>();
        services.AddScoped<SignalREntryEventSink>();
        services.AddScoped<IDataEventSink<Entry>>(sp =>
        {
            var sinks = new List<IDataEventSink<Entry>>
            {
                sp.GetRequiredService<SignalREntryEventSink>(),
            };
            var writeBack = sp.GetService<NightscoutEntryWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);

            return new CompositeDataEventSink<Entry>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Entry>>>());
        });
        services.AddScoped<IStateSpanService, StateSpanService>();
        services.AddScoped<ISleepService, SleepService>();
        services.AddScoped<ISleepReportService, SleepReportService>();
        services.AddScoped<DeviceStatusProjectionService>();
        services.AddScoped<IDataEventSink<DeviceStatus>>(sp =>
        {
            var sinks = new List<IDataEventSink<DeviceStatus>>();
            var writeBack = sp.GetService<NightscoutDeviceStatusWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<DeviceStatus>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<DeviceStatus>>>());
        });
        services.AddScoped<IBatteryService, BatteryService>();
        services.AddScoped<IProfileWriteService, ProfileWriteService>();
        services.AddScoped<IActiveProfileResolver, ActiveProfileResolver>();
        services.AddScoped<IBasalRateResolver, BasalRateResolver>();
        services.AddScoped<IBasalSegmentService, BasalSegmentService>();
        services.AddScoped<ISensitivityResolver, SensitivityResolver>();
        services.AddScoped<ICarbRatioResolver, CarbRatioResolver>();
        services.AddScoped<ITargetRangeResolver, TargetRangeResolver>();
        services.AddScoped<ITherapySettingsResolver, TherapySettingsResolver>();
        services.AddScoped<ITherapyTimelineResolver, TherapyTimelineResolver>();
        services.AddScoped<Services.Glucose.IProfileSnapshotService, Services.Glucose.ProfileSnapshotService>();
        services.AddScoped<ITempBasalResolver, TempBasalResolver>();
        services.AddScoped<IProfileProjectionService, ProfileProjectionService>();
        services.AddScoped<IDataEventSink<Profile>>(sp =>
        {
            var sinks = new List<IDataEventSink<Profile>>();
            var writeBack = sp.GetService<NightscoutProfileWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Profile>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Profile>>>());
        });

        // Food services
        services.AddScoped<IFoodService, FoodService>();
        services.AddScoped<IDataEventSink<Food>>(sp =>
        {
            var sinks = new List<IDataEventSink<Food>>();
            var writeBack = sp.GetService<NightscoutFoodWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Food>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Food>>>());
        });
        services.AddScoped<IConnectorFoodEntryService, ConnectorFoodEntryService>();
        services.AddScoped<ITreatmentFoodService, TreatmentFoodService>();
        services.AddScoped<IUserFoodFavoriteService, UserFoodFavoriteService>();
        services.AddScoped<IConnectorFoodEntryRepository, ConnectorFoodEntryRepository>();
        services.AddScoped<IMealMatchingService, MealMatchingService>();

        // Activity and health metric services
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IDataEventSink<Activity>>(sp =>
        {
            var sinks = new List<IDataEventSink<Activity>>();
            var writeBack = sp.GetService<NightscoutActivityWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Activity>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Activity>>>());
        });
        services.AddScoped<IHeartRateService, HeartRateService>();
        services.AddScoped<IBodyWeightService, BodyWeightService>();
        services.AddScoped<IStepCountService, StepCountService>();

        // Tracker services. The trigger is the IDeviceEventReactor adapter rather than a service any
        // caller invokes: it runs from the V4 device-event write chokepoint, which is what makes a
        // connector-ingested site change advance a tracker the same way a hand-entered one does.
        services.AddScoped<IDeviceEventReactor, TrackerTriggerService>();
        // Tracker notifications ride the alert engine: thresholds are synthesised into
        // managed tracker_age alert rules, backfilled once at startup for pre-existing
        // definitions (and self-healing if a managed rule is ever lost).
        services.AddScoped<ITrackerAlertRuleSyncService, TrackerAlertRuleSyncService>();
        services.AddHostedService<TrackerAlertRuleBackfillService>();
        services.AddScoped<ITrackerSuggestionService, TrackerSuggestionService>();
        services.AddScoped<IDeviceAgeService, DeviceAgeService>();

        // Device resolution
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IPatientDeviceStamper, PatientDeviceStamper>();
        services.AddScoped<IDeviceReattributionService, DeviceReattributionService>();

        // Canonical glucose stream (single-stream view for v1/v3, alarms, unfiltered analytics)
        services.AddScoped<ICanonicalGlucoseService, CanonicalGlucoseService>();
        services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();
        services.AddSingleton<AlertEvaluationWatermark>();

        // Coach marks
        services.AddScoped<ICoachMarkService, CoachMarkService>();

        // Timezone timeline (fake-UTC connector conversion + travel/relocation)
        services.AddScoped<ITimezoneTimelineService, TimezoneTimelineService>();

        // UI and display
        services.AddScoped<IUISettingsService, UISettingsService>();
        services.AddScoped<
            IMyFitnessPalMatchingSettingsService,
            MyFitnessPalMatchingSettingsService
        >();
        services.AddScoped<IClockFaceService, ClockFaceService>();
        services.AddScoped<IWidgetSummaryService, WidgetSummaryService>();
        // Basal series builder (used by chart data pipeline and reports endpoint)
        services.AddScoped<IBasalSeriesBuilder, BasalSeriesBuilder>();

        services.AddScoped<ProfileLoadStage>();
        services.AddScoped<DataFetchStage>();
        services.AddScoped<IobCobComputeStage>();
        services.AddScoped<DtoMappingStage>();

        // The stages run in the order of this array, not the order they were registered in.
        services.AddScoped<IEnumerable<IChartDataStage>>(sp => new IChartDataStage[]
        {
            sp.GetRequiredService<ProfileLoadStage>(),
            sp.GetRequiredService<DataFetchStage>(),
            sp.GetRequiredService<IobCobComputeStage>(),
            sp.GetRequiredService<DtoMappingStage>(),
        });

        services.AddScoped<IChartDataAssembler, DashboardChartDataAssembler>();
        services.AddScoped<IChartDataService, ChartDataService>();
        services.AddScoped<IActogramReportService, ActogramReportService>();
        services.AddScoped<IDataOverviewService, DataOverviewService>();

        return services;
    }

    /// <summary>
    /// V4 repositories, snapshot repositories, profile repositories,
    /// patient record repositories, AID detection, and decomposition pipeline.
    /// </summary>
    public static IServiceCollection AddV4Infrastructure(this IServiceCollection services)
    {
        // V4 Repositories
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IMeterGlucoseRepository, MeterGlucoseRepository>();
        services.AddScoped<ICalibrationRepository, CalibrationRepository>();
        services.AddScoped<IBolusRepository, BolusRepository>();
        services.AddScoped<IBasalInjectionRepository, BasalInjectionRepository>();
        services.AddScoped<ITempBasalRepository, TempBasalRepository>();
        services.AddScoped<ICarbIntakeRepository, CarbIntakeRepository>();
        services.AddScoped<IBGCheckRepository, BGCheckRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddScoped<IBolusCalculationRepository, BolusCalculationRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();

        // Manually-entered lab results (outside the V4 sync/dedup family)
        services.AddScoped<ILabHbA1cResultRepository, LabHbA1cResultRepository>();

        // V4 Snapshot Repositories
        services.AddScoped<IApsSnapshotRepository, ApsSnapshotRepository>();
        services.AddScoped<IPumpSnapshotRepository, PumpSnapshotRepository>();
        services.AddScoped<IUploaderSnapshotRepository, UploaderSnapshotRepository>();
        services.AddScoped<IDeviceStatusExtrasRepository, DeviceStatusExtrasRepository>();

        // V4 Profile Repositories
        services.AddScoped<ITherapySettingsRepository, TherapySettingsRepository>();
        services.AddScoped<IBasalScheduleRepository, BasalScheduleRepository>();
        services.AddScoped<ICarbRatioScheduleRepository, CarbRatioScheduleRepository>();
        services.AddScoped<ISensitivityScheduleRepository, SensitivityScheduleRepository>();
        services.AddScoped<ITargetRangeScheduleRepository, TargetRangeScheduleRepository>();

        // V4 Patient Record Repositories
        services.AddScoped<IPatientRecordRepository, PatientRecordRepository>();
        services.AddScoped<IPatientDeviceRepository, PatientDeviceRepository>();
        services.AddScoped<IPatientInsulinRepository, PatientInsulinRepository>();

        // Glucose processing
        services.AddScoped<IGlucoseProcessingConfigProvider, GlucoseProcessingConfigProvider>();
        services.AddScoped<IGlucoseProcessingResolver, GlucoseProcessingResolver>();

        // AID Detection Strategies and Metrics Service
        services.AddSingleton<IAidDetectionStrategy, ApsSnapshotStrategy>();
        services.AddSingleton<IAidDetectionStrategy, TbrBasedStrategy>();
        services.AddSingleton<IAidDetectionStrategy, NoAidStrategy>();
        services.AddScoped<IAidMetricsService, AidMetricsService>();

        // V4 Decomposers
        services.AddScoped<IEntryDecomposer, EntryDecomposer>();
        services.AddScoped<ITreatmentDecomposer, TreatmentDecomposer>();
        services.AddScoped<IDeviceStatusDecomposer, DeviceStatusDecomposer>();
        services.AddScoped<IActivityDecomposer, ActivityDecomposer>();
        services.AddScoped<IProfileDecomposer, ProfileDecomposer>();

        // Unified generic decomposer registrations
        services.AddScoped<IDecomposer<Entry>>(sp =>
            (IDecomposer<Entry>)sp.GetRequiredService<IEntryDecomposer>()
        );
        services.AddScoped<IDecomposer<Treatment>>(sp =>
            (IDecomposer<Treatment>)sp.GetRequiredService<ITreatmentDecomposer>()
        );
        services.AddScoped<IDecomposer<DeviceStatus>>(sp =>
            (IDecomposer<DeviceStatus>)sp.GetRequiredService<IDeviceStatusDecomposer>()
        );
        services.AddScoped<IDecomposer<Activity>>(sp =>
            (IDecomposer<Activity>)sp.GetRequiredService<IActivityDecomposer>()
        );
        services.AddScoped<IDecomposer<Profile>>(sp =>
            (IDecomposer<Profile>)sp.GetRequiredService<IProfileDecomposer>()
        );
        services.AddScoped<IDecompositionPipeline, DecompositionPipeline>();

        return services;
    }

    /// <summary>
    /// Real-time communication (SignalR), notifications (in-app, push, Loop/OpenAPS),
    /// and the notification resolution background service.
    /// </summary>
    public static IServiceCollection AddRealTimeAndNotifications(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // SignalR
        // Global hub filters have to be added to HubOptions — SignalR reads HubOptions.HubFilters
        // and never resolves IHubFilter from the container, so a filter registered only in DI never
        // runs. TenantHubFilter is outermost so the invocation scope's ITenantAccessor is populated
        // before anything inside resolves a tenant-scoped service; HubAuthorizationFilter needs none
        // of that itself, since it reads the credential out of HttpContext.Items.
        services.AddSignalR(options =>
        {
            Microsoft.AspNetCore.SignalR.HubOptionsExtensions
                .AddFilter<Nocturne.API.Hubs.TenantHubFilter>(options);
            Microsoft.AspNetCore.SignalR.HubOptionsExtensions
                .AddFilter<Nocturne.API.Hubs.HubAuthorizationFilter>(options);
        });
        services.AddSingleton<Nocturne.API.Hubs.TenantHubFilter>();
        services.AddSingleton<Nocturne.API.Hubs.HubAuthorizationFilter>();
        services.AddScoped<ISignalRBroadcastService, SignalRBroadcastService>();
        services.AddScoped<ISyncProgressReporter, SignalRSyncProgressReporter>();

        // Native V4 record broadcasting (companion + Prelude) over the glucose/care/device/therapy
        // categories. Open generic so every V4 model type resolves; the repository chokepoint fires it
        // for live writes only. Additive to the legacy v1 IDataEventSink<T> projections above.
        services.AddScoped(typeof(IV4RecordBroadcaster<>), typeof(SignalRV4RecordBroadcaster<>));

        // Push notifications
        services.AddScoped<INotificationV2Service, NotificationV2Service>();
        services.AddScoped<INotificationV1Service, NotificationV1Service>();
        services.AddScoped<IApnsClientFactory, ApnsClientFactory>();
        services.AddHttpClient(
            "dotAPNS",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }
        );

        // Loop/OpenAPS integration
        services.Configure<LoopConfiguration>(configuration.GetSection("Loop"));
        services.AddScoped<ILoopService, LoopService>();
        services.AddScoped<IOpenApsService, OpenApsService>();
        services.AddScoped<IPumpAlertService, PumpAlertService>();

        // In-app notifications
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped<IInAppNotificationService, InAppNotificationService>();
        services.AddHostedService<NotificationResolutionService>();
        services.AddHostedService<NotificationCleanupService>();

        // Notification template registry (singleton -- templates are immutable after startup)
        var templateRegistry = new NotificationTemplateRegistry().AddBuiltInTemplates();
        services.AddSingleton<INotificationTemplateRegistry>(templateRegistry);

        // Client device registry (Prelude/Companion actuation targets)
        services.AddScoped<
            Nocturne.Core.Contracts.ClientDevices.IClientDeviceService,
            Nocturne.API.Services.ClientDevices.ClientDeviceService>();

        // Notification action handlers (scoped -- they may depend on scoped services)
        services.AddScoped<INotificationActionHandler, MealMatchActionHandler>();
        services.AddScoped<INotificationActionHandler, TrackerSuggestionActionHandler>();
        services.AddScoped<INotificationActionHandler, AlertActionHandler>();

        return services;
    }

    /// <summary>
    /// Alert engines, device health monitoring, compression low detection,
    /// and all notifier implementations (SignalR, webhook, Pushover).
    /// </summary>
    public static IServiceCollection AddAlertingAndMonitoring(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Compression low detection
        services.AddScoped<ICompressionLowRepository, CompressionLowRepository>();
        services.AddScoped<ICompressionLowService, CompressionLowService>();
        services.AddSingleton<CompressionLowDetectionService>();
        services.AddSingleton<ICompressionLowDetectionService>(sp =>
            sp.GetRequiredService<CompressionLowDetectionService>()
        );
        services.AddHostedService(sp => sp.GetRequiredService<CompressionLowDetectionService>());

        // Webhook infrastructure (reused by new alert engine)
        services.AddScoped<WebhookRequestSender>();

        // Webhook targets are supplied by whoever is signed in. Nothing re-checks a hop here, so
        // redirects are off outright rather than followed by a guard — a target answering 307 with
        // http://169.254.169.254/ or an internal service name would otherwise be fetched from
        // inside the deployment network.
        services.AddHttpClient(WebhookRequestSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback =
                    new PinnedConnector(OutboundAddressPolicy.PubliclyRoutable).ConnectAsync,
            });

        // Condition evaluators. Scoped because SustainedEvaluator depends on the scoped
        // IConditionTimerStore (DbContext-backed); the registry is also scoped because it captures
        // IEnumerable<IConditionEvaluator>.
        services.AddAlertEvaluators();
        services.AddScoped<ConditionEvaluatorRegistry>();

        // Sustained-condition timer store
        services.AddScoped<IConditionTimerStore, ConditionTimerRepository>();

        // The excursion tracker's per-rule serialisation gate is a singleton: the sweep and the
        // per-reading path evaluate the same rule from different scopes.
        services.AddSingleton<AlertRuleEvaluationGate>();

        // Alert evaluation engine and excursion tracker seams (Alerts:Engine = managed | shadow | rust)
        services.AddAlertEvaluationEngine(configuration);

        // Alert engine core
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.Configure<AlertEvaluationOptions>(
            configuration.GetSection(AlertEvaluationOptions.SectionName));
        services.AddScoped<IReservoirEstimationService, ReservoirEstimationService>();
        // Bundles the enricher's data-source dependencies; resolved positionally from DI.
        services.AddScoped<SensorContextEnricherDependencies>();
        services.AddScoped<ISensorContextEnricher, SensorContextEnricher>();
        services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();
        services.AddScoped<IAlertDeliveryService, AlertDeliveryService>();
        services.AddScoped<IAlertAcknowledgementService, AlertAcknowledgementService>();
        services.AddScoped<IExcursionResolutionHandler, ExcursionResolutionHandler>();
        services.AddScoped<IAlertReferenceService, AlertReferenceService>();
        services.AddScoped<IAlertReplayService, AlertReplayService>();
        // Scope-class classification (scoped Do Not Disturb, ADR 0004): stateless over
        // the static native engine, so a singleton. Backfilled once at startup.
        services.AddSingleton<IRuleScopeClassifier, RuleScopeClassifier>();
        services.AddHostedService<RuleScopeClassBackfillService>();
        services.AddSingleton<IAlertRuleConditionValidator, AlertRuleConditionValidator>();
        services.AddHostedService<AlertRuleConditionAuditService>();

        // Delivery providers
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.WebPushProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.InAppProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.WebhookProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.ChatBotProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.HomeAssistantProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.DeviceActionProvider>();
        services.AddHttpClient("ChatBot");

        // Chat identity
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityService>();
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityDirectoryService>();
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityPendingLinkService>();
        services.AddHostedService<Nocturne.API.Services.Chat.ChatIdentityPendingLinkCleanupService>();

        // Bot health tracking
        services.AddSingleton<BotHealthService>();

        // Background sweep
        services.AddHostedService<AlertSweepService>();

        // Periodic cursor-driven deduplication reconciliation across active tenants
        services.AddHostedService<Nocturne.API.Services.BackgroundServices.DeduplicationReconciliationBackgroundService>();

        return services;
    }

    /// <summary>
    /// Data source connectors, deduplication, secret encryption,
    /// connector sync, and demo service health monitoring.
    /// </summary>
    public static IServiceCollection AddConnectorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddScoped<IDataSourceService, DataSourceService>();
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddSingleton<ISecretEncryptionService, SecretEncryptionService>();
        services.AddScoped<IConnectorConfigurationService, ConnectorConfigurationService>();
        services.AddScoped<IConnectorSyncCursorStore, ConnectorSyncCursorStore>();
        services.AddScoped<PlatformSettingsService>();
        services.AddScoped<IConnectorSyncService, ConnectorSyncService>();
        services.AddScoped<IConnectorCursorResetService, ConnectorCursorResetService>();
        // Singleton: holds the in-memory job registry so a reset started by one request can be
        // polled by later requests. Creates its own DI scopes for the scoped reset engine.
        services.AddSingleton<IConnectorCursorResetJobService, ConnectorCursorResetJobService>();

        // Connector runtime
        services.AddBaseConnectorServices();
        services.AddScoped<IGlucosePublisher, GlucosePublisher>();
        services.AddScoped<ITreatmentPublisher, TreatmentPublisher>();
        services.AddScoped<IDevicePublisher, DevicePublisher>();
        services.AddScoped<IMetadataPublisher, MetadataPublisher>();
        services.AddScoped<IConnectorPublisher, InProcessConnectorPublisher>();
        services.AddConnectors(
            configuration,
            pollingService: typeof(ConnectorBackgroundService<,>)
        );
        services.AddSingleton(ConnectorSyncBudget.FromConfiguration(configuration, services));
        // IMeterFactory comes from the host; AddMetrics keeps the registration self-sufficient for a
        // host that has not enabled the OpenTelemetry metrics pipeline.
        services.AddMetrics();
        services.AddSingleton<ConnectorSyncMetrics>();
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(ConnectorSyncMetrics.MeterName));
        // After AddConnectors: the installers register the token caches as IConnectorCacheInvalidator
        // with TryAddSingleton, which a prior registration of the interface would silently suppress.
        services.AddSingleton<ConnectorPollerNudge>();
        services.AddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<ConnectorPollerNudge>());

        // Demo service health monitor
        services.AddHttpClient("DemoServiceHealth");
        services.AddHostedService<DemoServiceHealthMonitor>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine"/> and
    /// <see cref="IExcursionTracker"/> seams: all three engine implementations, the tracker
    /// deciding with the selected engine, plus the singleton
    /// <see cref="Nocturne.API.Services.Alerts.Engines.AlertEngineSelection"/> resolved
    /// from the <c>Alerts:Engine</c> flag (<c>managed</c> | <c>shadow</c> | <c>rust</c>,
    /// default <c>managed</c>). Program resolves the selection at startup so the native-library
    /// probe runs once, before the host serves traffic; see
    /// <see cref="Nocturne.API.Services.Alerts.Engines.AlertEngineSelector"/> for what a failed
    /// probe does in each mode.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration carrying the <c>Alerts:Engine</c> flag.</param>
    /// <param name="nativeProbe">
    /// Native-library probe override for tests; defaults to
    /// <see cref="Nocturne.Core.Alerts.Native.AlertsInterop.Probe"/>.
    /// </param>
    public static IServiceCollection AddAlertEvaluationEngine(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<Nocturne.Core.Alerts.Native.NativeProbeResult>? nativeProbe = null)
    {
        services.AddMetrics();
        services.AddSingleton<Nocturne.API.Services.Alerts.Engines.AlertEngineErrors>();
        services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddMeter(Nocturne.API.Services.Alerts.Engines.AlertEngineErrors.MeterName));
        services.AddHealthChecks()
            .AddCheck<Nocturne.API.Services.Alerts.Engines.AlertEngineHealthCheck>("alert-engine");

        services.AddScoped<Nocturne.API.Services.Alerts.Engines.ManagedAlertEngine>();
        services.AddScoped<Nocturne.API.Services.Alerts.Engines.RustBackedAlertEngine>();
        services.AddScoped<
            Nocturne.API.Services.Alerts.Engines.IShadowRuleEvaluator,
            Nocturne.API.Services.Alerts.Engines.RustShadowRuleEvaluator>();
        services.AddScoped<Nocturne.API.Services.Alerts.Engines.ShadowAlertEngine>();

        // The managed engine always tracks with the managed decider; shadow mode compares
        // through ShadowAlertEngine instead.
        services.AddScoped(sp => new ExcursionTracker(
            sp.GetRequiredService<Nocturne.Core.Contracts.Repositories.IAlertTrackerRepository>(),
            sp.GetRequiredService<AlertRuleEvaluationGate>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ExcursionTracker>>()));
        services.AddScoped<IExcursionTracker>(sp =>
        {
            var mode = sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineSelection>().Mode;
            if (mode == Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Managed)
                return sp.GetRequiredService<ExcursionTracker>();

            var errors = sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineErrors>();
            IExcursionDecider decider = mode == Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Rust
                ? new Nocturne.API.Services.Alerts.Engines.RustExcursionDecider(
                    errors, Nocturne.API.Services.Alerts.Engines.AlertEngineErrors.RustEngine)
                : new Nocturne.API.Services.Alerts.Engines.ShadowExcursionDecider(
                    ManagedExcursionDecider.Instance,
                    new Nocturne.API.Services.Alerts.Engines.RustExcursionDecider(
                        errors, Nocturne.API.Services.Alerts.Engines.AlertEngineErrors.ShadowEngine),
                    sp.GetRequiredService<ILogger<Nocturne.API.Services.Alerts.Engines.ShadowExcursionDecider>>());
            return new ExcursionTracker(
                sp.GetRequiredService<Nocturne.Core.Contracts.Repositories.IAlertTrackerRepository>(),
                sp.GetRequiredService<AlertRuleEvaluationGate>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ExcursionTracker>>(),
                decider);
        });

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(Nocturne.API.Services.Alerts.Engines.AlertEngineSelector).FullName!);
            return Nocturne.API.Services.Alerts.Engines.AlertEngineSelector.Select(
                configuration[Nocturne.API.Services.Alerts.Engines.AlertEngineSelector.ConfigurationKey],
                nativeProbe ?? Nocturne.Core.Alerts.Native.AlertsInterop.Probe,
                logger);
        });

        services.AddSingleton<Nocturne.API.Services.Alerts.Engines.ManagedAlertReplayEngine>();
        services.AddSingleton<Nocturne.Core.Contracts.Alerts.IAlertReplayEngine>(sp =>
        {
            var managed = sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.ManagedAlertReplayEngine>();
            var errors = sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineErrors>();
            return sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineSelection>().Mode switch
            {
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Rust =>
                    new Nocturne.API.Services.Alerts.Engines.RustAlertReplayEngine(errors),
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Shadow =>
                    new Nocturne.API.Services.Alerts.Engines.ShadowAlertReplayEngine(
                        managed,
                        new Nocturne.API.Services.Alerts.Engines.RustAlertReplayEngine(errors)
                        {
                            EngineTag = Nocturne.API.Services.Alerts.Engines.AlertEngineErrors.ShadowEngine,
                        },
                        sp.GetRequiredService<ILogger<Nocturne.API.Services.Alerts.Engines.ShadowAlertReplayEngine>>()),
                _ => managed,
            };
        });

        services.AddScoped<Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine>(sp =>
            sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineSelection>().Mode switch
            {
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Rust =>
                    sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.RustBackedAlertEngine>(),
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Shadow =>
                    sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.ShadowAlertEngine>(),
                _ => sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.ManagedAlertEngine>(),
            });

        return services;
    }

    /// <summary>
    /// Registers every <see cref="IConditionEvaluator"/> implementation that the
    /// <see cref="ConditionEvaluatorRegistry"/> resolves at runtime. Extracted from
    /// <see cref="AddAlertingAndMonitoring"/> so production wiring and the registry
    /// coverage tests can share the same single source of truth — adding a new
    /// evaluator here automatically updates both.
    /// </summary>
    public static IServiceCollection AddAlertEvaluators(this IServiceCollection services)
    {
        services.AddScoped<IConditionEvaluator, ThresholdEvaluator>();
        services.AddScoped<IConditionEvaluator, RateOfChangeEvaluator>();
        services.AddScoped<IConditionEvaluator, SignalLossEvaluator>();
        services.AddScoped<IConditionEvaluator, StalenessEvaluator>();
        services.AddScoped<IConditionEvaluator, CompositeEvaluator>();
        services.AddScoped<IConditionEvaluator, NotEvaluator>();
        services.AddScoped<IConditionEvaluator, SustainedEvaluator>();
        services.AddScoped<IConditionEvaluator, PredictedEvaluator>();
        services.AddScoped<IConditionEvaluator, TrendEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeOfDayEvaluator>();
        services.AddScoped<IConditionEvaluator, IobEvaluator>();
        services.AddScoped<IConditionEvaluator, CobEvaluator>();
        services.AddScoped<IConditionEvaluator, ReservoirEvaluator>();
        services.AddScoped<IConditionEvaluator, SiteAgeEvaluator>();
        services.AddScoped<IConditionEvaluator, SensorAgeEvaluator>();
        services.AddScoped<IConditionEvaluator, AlertStateEvaluator>();
        services.AddScoped<IConditionEvaluator, LoopStaleEvaluator>();
        services.AddScoped<IConditionEvaluator, LoopEnactionStaleEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpSuspendedEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpBatteryEvaluator>();
        services.AddScoped<IConditionEvaluator, TempBasalEvaluator>();
        services.AddScoped<IConditionEvaluator, UploaderBatteryEvaluator>();
        services.AddScoped<IConditionEvaluator, OverrideActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, SensitivityRatioEvaluator>();
        services.AddScoped<IConditionEvaluator, DoNotDisturbEvaluator>();
        services.AddScoped<IConditionEvaluator, GlucoseBucketEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeSinceLastCarbEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeSinceLastBolusEvaluator>();
        services.AddScoped<IConditionEvaluator, DayOfWeekEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpStateEvaluator>();
        services.AddScoped<IConditionEvaluator, StateSpanActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, SleepSessionActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, TrackerAgeEvaluator>();
        return services;
    }

    /// <summary>
    /// Migration job service and startup migration check.
    /// </summary>
    public static IServiceCollection AddMigrationServices(this IServiceCollection services)
    {
        services.AddSingleton<
            Nocturne.API.Services.Migration.IMigrationJobService,
            Nocturne.API.Services.Migration.MigrationJobService
        >();
        services.AddHostedService<Nocturne.API.Services.Migration.MigrationStartupService>();

        // The Nightscout to migrate from is a tenant-admin-supplied URL, the same shape as a
        // connector base URL, so it takes the connector client. That also puts redirects under the
        // guard, which drops the tenant's api-secret when a hop crosses origin — .NET's own redirect
        // handling strips Authorization but not api-secret.
        services.AddHttpClient(Nocturne.API.Services.Migration.MigrationJobService.HttpClientName)
            .ConfigureConnectorClient(baseUrl: null, userAgent: "Nocturne-Migration/1.0");

        return services;
    }
}
