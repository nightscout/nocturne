using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Adds human-readable descriptions to OpenAPI tags so Scalar displays an overview
/// for each controller group. Descriptions use GitHub-flavored markdown.
/// Embeds ER diagrams from the diagram manifest into matching tag descriptions.
/// </summary>
public sealed class TagDescriptionDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly Dictionary<string, List<DiagramRef>> _tagDiagrams;

    public TagDescriptionDocumentTransformer(IWebHostEnvironment env)
    {
        _tagDiagrams = BuildTagDiagramMap(env);
    }

    private static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["PlatformAdmin"] = "Platform Admin",
        ["TenantAdmin"] = "Tenant Admin",
        ["V1"] = "Nightscout V1",
        ["V2"] = "Nightscout V2",
        ["V3"] = "Nightscout V3",
    };

    /// <summary>
    /// Conceptual guide tags that have no operations of their own but still render as
    /// a standalone sidebar page from their description. Added to the Nocturne document only.
    /// </summary>
    private static readonly string[] StandaloneDocTags = ["Syncing"];

    private static readonly Dictionary<string, string> Descriptions = new()
    {
        // ── Nocturne document (V4 + Auth) ────────────────────────────────

        ["Authentication"] = """
            Sign-in, token management, and multi-factor authentication.

            Covers five authentication mechanisms:

            - **OAuth 2.0** — Authorization Code + PKCE and Device Authorization Grant (RFC 8628). All clients are public; PKCE is mandatory — there are no client secrets.
            - **OIDC** — Federated login via external identity providers, callback handling, and session management.
            - **Passkeys** — WebAuthn/FIDO2 registration and login ceremonies (discoverable and non-discoverable credentials), plus recovery codes.
            - **TOTP** — Time-based one-time password setup, verification, and credential lifecycle.
            - **Direct Grants** — Programmatic API tokens (prefixed `noc_`) for headless / automation use cases. These bypass OAuth entirely. Legacy Nightscout API secrets (SHA-1 hashes) are automatically migrated into equivalent direct grants.

            > **Footgun:** Direct grant tokens are long-lived and have no automatic expiry. Treat them like passwords.
            """,

        ["OIDC Discovery"] = """
            Standard `.well-known` endpoints that make Nocturne act as its own OAuth 2.0 / OIDC issuer.

            Returns the OpenID Provider configuration (`openid-configuration`) and JSON Web Key Set (`jwks.json`). These endpoints are **unauthenticated** by design — they must be publicly reachable for token validation.
            """,

        ["Analytics"] = """
            Pre-computed data for dashboards, charts, and retrospective analysis.

            Key endpoints:

            - **Chart Data** — Returns *everything* the glucose chart needs in a single call: readings, IOB/COB series, basal delivery, treatment markers, state spans, system events, and tracker markers. Prefer this over calling individual endpoints.
            - **Correlation** — Query across all V4 repositories by correlation ID to trace related records.
            - **Data Overview** — Year-level availability and day-level record counts for heatmap visualisation.
            - **Predictions** — Glucose forecasts from DeviceStatus sources (AAPS / Trio / Loop) or the OrefWasm engine.
            - **Retrospective** — Day-in-review snapshots combining IOB, COB, glucose, basal timelines, and insulin delivery at specific points in time.
            - **Statistics** — Aggregated statistics including glucose time-in-range, insulin delivery breakdowns, and AID system metrics.
            - **Summary** — Widget-friendly data designed for mobile widgets, watch faces, and other constrained displays.
            - **Analytics** — Transparency controls for analytics collection — view, configure, and opt out.
            """,

        ["Connectors"] = """
            Configuration management for data source connectors (Dexcom, Glooko, Libre, etc.).

            > **Internal only.** These endpoints are intended for server-to-server use by connector services via mTLS. They are not designed for end-user consumption and will eventually be gated behind mTLS authentication.
            """,

        ["Devices"] = """
            Device telemetry and consumable age tracking.

            - **Battery** — Track and analyse battery status across diabetes devices.
            - **Device Age** — CAGE (cannula), SAGE (sensor), IAGE (insulin reservoir), and BAGE (battery) age tracking, backed by the V4 DeviceEvents system.
            """,

        ["Glucose"] = """
            V4 glucose data: sensor readings, meter checks, calibrations, and blood glucose checks.

            - **Sensor Glucose** — Continuous glucose monitor (CGM) readings.
            - **Meter Glucose** — Fingerstick blood glucose meter readings.
            - **Calibrations** — CGM calibration records.
            - **BG Checks** — Point-in-time blood glucose checks from any source.
            """,

        ["Health"] = """
            Biometric and activity data beyond glucose.

            - **Heart Rate** — Heart rate readings from diabetes apps and wearables.
            - **Step Count** — Step count data from diabetes apps and wearables.
            - **Body Weight** — Weight and body composition time-series.
            - **Patient Record** — Patient metadata: records, devices, and insulin formulations in use.
            """,

        ["Identity"] = """
            Multi-tenancy, membership, roles, guest access, and cross-platform identity linking.

            - **My Tenants** — List tenants the authenticated user belongs to.
            - **My Permissions** — Effective permissions for the current tenant, computed from roles intersected with token scopes.
            - **Roles** — RBAC role and permission management.
            - **Member Invites** — Invite links, member listing, and role assignment.
            - **Guest Links** — Temporary 48-hour read-only access links for data sharing. Recipients activate a short code to receive a scoped session cookie.
            - **Connected Apps** — OAuth app grants ("connected apps") for the authenticated user.
            - **Linked Platforms** — Cross-platform identity linking for the authenticated user.
            - **Chat Identity** — Tenant-scoped linking of chat platform accounts (Discord, Telegram, etc.).
            - **Chat Identity Directory** — Cross-tenant directory for routing chat platform identities to the correct tenant. Server-to-server only.

            > **Footgun:** The Chat Identity Directory operates cross-tenant and is authenticated by instance key, not user tokens. Do not expose it to end users.
            """,

        ["Monitoring"] = """
            Alerting, notifications, and flexible tracker management.

            - **Alert Rules** — CRUD for alert rules with nested schedules, escalation steps, and notification channels.
            - **Alerts** — Active alert state, history, and acknowledgement.
            - **Alert Invites** — Shareable invite links that grant others permission to receive your alerts.
            - **Alert Custom Sounds** — Upload, list, stream, and delete custom alert audio files.
            - **Tracker Alerts** — Alerts tied to tracker events (e.g. "site change overdue").
            - **Trackers** — Flexible tracker management for consumables, appointments, and reminders.
            - **Notifications** — In-app notification delivery and management.
            """,

        ["Platform"] = """
            System-level status, diagnostics, and service metadata.

            - **Status** — V4 JSON status endpoint with detailed system information.
            - **System** — Service health and coordination endpoints.
            - **System Events** — Point-in-time system events (alarms, warnings, info).
            - **Services** — Metadata about available data sources, connectors, and integrations.
            - **Compatibility** — Dashboard data for Nightscout compatibility analysis.
            - **Debug** — Query inspection and MongoDB query debugging tools.
            - **API Secret** — Legacy API secret management.

            > **Footgun:** Debug endpoints expose raw query details and are intended for development use. They should be disabled or restricted in production deployments.
            """,

        ["PlatformAdmin"] = """
            Super-admin tenant lifecycle management.

            Provides tenant creation, listing, and administration for platform operators. These endpoints require platform-level admin privileges — they are not accessible to regular tenant users.
            """,

        ["State Spans"] = """
            Time-ranged system states and user-annotated activity periods.

            Records continuous state windows such as pump modes, connectivity periods, temporary targets, overrides, active profile switches, and user-annotated activities (sleep, exercise, illness, travel). State spans are created automatically by connector ingest pipelines or manually via this API.

            Convenience sub-routes pre-filter by category: `/pump-modes`, `/connectivity`, `/overrides`, `/temporary-targets`, `/profiles`, `/sleep`, `/exercise`, `/illness`, `/travel`, `/activities`.
            """,

        ["Profiles"] = """
            User and therapy configuration.

            - **Therapy Settings** — Core therapy configuration (DIA, units, etc.).
            - **Basal Schedules** — Time-of-day basal rate schedules.
            - **Carb Ratio Schedules** — Time-of-day insulin-to-carb ratio schedules.
            - **Sensitivity Schedules** — Time-of-day insulin sensitivity factor schedules.
            - **Target Range Schedules** — Time-of-day target glucose range schedules.
            - **Glucose Processing Settings** — Glucose data processing configuration (smoothing, calibration, noise filtering).
            - **UI Settings** — Aggregated frontend configuration from multiple sources (units, ranges, display preferences).
            - **User Preferences** — Per-user preference storage.
            - **Clock Faces** — Watch face configuration management.
            - **MyFitnessPal Settings** — Global settings for MyFitnessPal food matching integration.
            """,

        ["TenantAdmin"] = """
            Administrative operations for tenant data management, migration, and maintenance.

            - **Migration** — Import data from a Nightscout MongoDB instance.
            - **Nightscout Transition** — Aggregated migration progress and write-compatibility status for the migration dashboard.
            - **Backfill** — Decompose all existing legacy entries and treatments into V4 granular tables.
            - **Deduplication** — Run and monitor deduplication jobs across data tables.
            - **Discrepancy** — Compatibility analysis between legacy and V4 data representations.
            - **Compression Low** — Detect and review compression low artefacts in CGM data.
            - **Processing** — Async processing job status tracking.
            - **OIDC Provider Admin** — Manage OIDC identity provider configurations for the tenant.
            - **Subject Admin** — Manage user/subject records within the tenant.

            > **Footgun:** The Backfill endpoint decomposes *all* legacy data. On large datasets this is a long-running operation — it runs asynchronously and progress can be tracked via the Processing endpoints.
            """,

        ["Treatments"] = """
            V4 treatment data: boluses, nutrition, notes, and meal tracking.

            - **Boluses** — Insulin bolus records with calculator context.
            - **Bolus Calculations** — Bolus calculator input/output records for audit and replay.
            - **Nutrition** — Carbohydrate intakes, food breakdown, and meal records.
            - **Foods** — Food favourites, recent foods, and food lifecycle management.
            - **Connector Food Entries** — Food entries imported by external connectors.
            - **Meal Matching** — Match nutrition data to treatment events.
            - **Notes** — Free-text observation records.
            """,

        ["Metadata"] = """
            Static, read-only reference catalogs for populating app UI with prefilled lists.

            - **Device Catalog** — Known pump, CGM, and meter hardware models, filterable by category.
            - **Insulin Catalog** — Insulin formulations with pharmacokinetic profiles (onset, peak, duration).

            This is reference data — it is not tenant-specific and cannot be modified via the API.
            """,

        ["Syncing"] = """
            How Nocturne ingests data from connectors, how it resumes where it left off, and why re-syncing the same data never creates duplicates.

            ## How connectors sync

            Each connector (Nightscout, Dexcom, Glooko, …) runs as a per-tenant background service. The poller wakes on the tenant's configured interval (and, for Nightscout, immediately on real-time socket events from the upstream instance) and pulls any new data since it last ran. There is no stored cursor — Nocturne derives the resume point from the **latest record already stored** for each data type.

            ## Per-type catch-up cursors

            A connection carries several independent streams — glucose, treatments (boluses/carbs/manual BG), device status, activity — and each resumes from **its own** latest record, not a single shared cursor. This matters because one stream can legitimately lag another: a closed-loop user produces a glucose reading every 5 minutes but may bolus only a few times a day. Tying every stream to the glucose cursor would strand the slower streams permanently once glucose caught up.

            - On a normal background sync each enabled type fetches from `latest_stored_record − 5min` (a small overlap absorbs clock drift).
            - When a type has **no** data yet, it performs an initial backfill over a bounded window (currently 6 months) so a new connection fills in recent history.
            - Profiles and food are small and fetched in full on every sync rather than windowed.

            ## Resilient ingestion

            Real-world uploader payloads are messy: numeric fields arrive as JSON strings (`"insulin":"1.5"`), booleans as `"true"`/`1`, direction as an integer. Connector deserialization tolerates these variants so one oddly-typed record can't throw and abort an entire page — which would otherwise drop a whole stream's backfill while a sibling stream (e.g. glucose) succeeded.

            ## Re-syncing and cursor reset

            Two endpoints force data in on demand, both reusing the stored connector credentials:

            - **Manual sync** — `POST /api/v4/services/connectors/{id}/sync` with a `SyncRequest`. Supplying `to` switches the connector into *explicit-range* mode: it fetches exactly `from`…`to` for the requested `dataTypes`, bypassing the catch-up cursors. Use this to backfill a specific window of a specific type (e.g. treatments only) without re-pulling everything.
            - **Cursor reset** — `POST /api/v4/services/connectors/{id}/reset-cursor`. Re-pulls history (all types, or a subset) from the beginning, or from an optional `from`. Use this after fixing a connector bug to push corrected data to a tenant.

            Both are safe to re-run because of idempotency, below.

            ## Idempotency

            How Nocturne guarantees that writing the same record twice never creates a duplicate — and why there are two separate keys for it.

            Diabetes data is written by two very different kinds of client, and **both retry**. A phone app may resend a `POST` after a flaky network; a background connector re-fetches overlapping time windows on every sync. To make writes safe to repeat, every V4 record carries an idempotency key and the database enforces uniqueness on it. Which key is used depends on **who is writing**.

            ### Two channels

            | Channel | Key | Who sets it | Why |
            | --- | --- | --- | --- |
            | **API clients** (apps, uploaders) | `syncIdentifier` | The client | The client owns a stable per-record ID and sends it with every write. |
            | **Connectors** (Glooko, Dexcom, …) | `legacyId` | Nocturne | The upstream source exposes no stable per-record ID, so a deterministic hash of the record's content is derived. |

            A given record normally carries **one** of these keys, not both.

            #### `syncIdentifier` — client-owned

            Clients that integrate over the REST API supply their own stable identifier on each write, scoped by `dataSource`. On create, if a record with the same `(dataSource, syncIdentifier)` already exists, the endpoint returns the existing record unchanged — an idempotent upsert — instead of inserting a duplicate. Enforced by a unique index on `(tenant_id, data_source, sync_identifier)`.

            Use this when **you** control the writer and can guarantee a stable ID that survives retries.

            #### `legacyId` — content-derived

            Connectors pull from upstream platforms that rarely expose a stable per-record ID — Glooko's graph series, for example, are just timestamped points. Rather than a source ID, the connector derives a deterministic fingerprint from the record's own immutable content (event type + timestamp + salient fields). Re-syncing the same window regenerates an identical `legacyId`, so the second write is recognised as a duplicate. Enforced by a unique index on `(tenant_id, legacy_id)`.

            `legacyId` also doubles as migration traceability: it ties a V4 record back to the original Nightscout/Mongo document it was decomposed from.

            ### Guidance for writers

            - **Building a client or uploader?** Always send `dataSource` + `syncIdentifier`, and reuse the same `syncIdentifier` when retrying the same logical record.
            - **Building a connector?** Derive a stable `legacyId` from immutable content. The repository bulk-create paths dedupe on it automatically.

            > **Footgun:** The two keys are not interchangeable per record type. A writer that populates `legacyId` but is backed by a repository that only dedupes on `syncIdentifier` (or vice-versa) will insert duplicates — or hit a unique-constraint violation on the next retry. When adding a new record type, make sure its repository honours **both** keys.
            """,

        // ── Nightscout document (V1 / V2 / V3) ──────────────────────────

        ["V1"] = """
            Legacy Nightscout V1 API — **1:1 compatible** with the original JavaScript implementation.

            Covers the core Nightscout data model: entries (SGV, MBG, CAL), treatments (bolus, temp basal, carb corrections, site changes), profiles, device status, and food records. Also includes Alexa voice assistant integration and Pebble smartwatch endpoints.

            All writes decompose into V4 granular models — there are no standalone legacy tables. Reads project back from V4 data into the legacy shape.

            > **Timestamps:** V1 uses a "mills-first" convention. Clients that write entries must provide `date` in epoch milliseconds.

            > **Authentication:** V1 endpoints accept the legacy `api_secret` header (SHA-1 hash) or token-based auth via `?token=` query parameter. Both are supported for backwards compatibility.
            """,

        ["V2"] = """
            Enhanced Nightscout V2 API — maintains compatibility with legacy V2 consumers.

            - **Authorization** — Token permission checking.
            - **DData** — Direct data access for aggregated reads.
            - **Loop** — Apple Push Notification Service (APNS) integration for the iOS Loop app.
            - **Notifications** — Enhanced notification system with push support.
            - **Properties** — Client properties and runtime settings.
            - **Summary** — Aggregated data endpoints for dashboard widgets.
            """,

        ["V3"] = """
            Nightscout V3 RESTful API — full CRUD with `Last-Modified` / `If-Modified-Since` support.

            Provides a consistent RESTful interface across all core collections: entries, treatments, device status, food, profiles, and settings. Each collection supports filtering, pagination, field projection, and soft-delete semantics. All writes decompose into V4 granular models — reads project back from V4 data into the legacy shape.

            - **Last Modified** — Timestamps for when each collection was last modified, enabling efficient polling via conditional requests.
            - **Status** — Extended status with permissions and authorization details.
            - **Version** — Server version information.

            > **Note:** V3 uses `identifier` (a string field) as the primary key for records, not the MongoDB `_id`. When migrating from V1, ensure your client uses the correct identifier field.
            """,
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Collect every tag name already referenced by operations.
        var usedTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null) continue;
            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.Tags is null) continue;
                foreach (var tag in operation.Tags)
                {
                    if (tag is IOpenApiTag openApiTag && openApiTag.Name is not null)
                        usedTags.Add(openApiTag.Name);
                }
            }
        }

        // Standalone conceptual guide pages have no operations but still render as
        // their own sidebar entry. Only surface them in the Nocturne document.
        if (context.DocumentName == "nocturne")
        {
            foreach (var docTag in StandaloneDocTags)
                usedTags.Add(docTag);
        }

        // Build the document-level tag set with descriptions.
        var tags = new HashSet<OpenApiTag>(TagNameComparer.Instance);

        foreach (var tagName in usedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            Descriptions.TryGetValue(tagName, out var description);

            // Append any ER diagrams mapped to this tag.
            if (_tagDiagrams.TryGetValue(tagName, out var diagrams))
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    sb.AppendLine(description);
                    sb.AppendLine();
                }

                sb.AppendLine("## Data Model");
                sb.AppendLine();

                foreach (var diagram in diagrams)
                {
                    sb.AppendLine($"**{diagram.Title}**");
                    if (!string.IsNullOrWhiteSpace(diagram.Description))
                        sb.AppendLine($"_{diagram.Description}_");
                    sb.AppendLine();
                    sb.AppendLine("```mermaid");
                    sb.AppendLine(diagram.MermaidSource);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                description = sb.ToString().TrimEnd();
            }

            var tagObj = new OpenApiTag
            {
                Name = tagName,
                Description = description,
            };

            if (DisplayNames.TryGetValue(tagName, out var displayName))
            {
                tagObj.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                tagObj.Extensions["x-displayName"] = new JsonNodeExtension(JsonValue.Create(displayName));
            }

            tags.Add(tagObj);
        }

        document.Tags = tags;

        return Task.CompletedTask;
    }

    private static Dictionary<string, List<DiagramRef>> BuildTagDiagramMap(IWebHostEnvironment env)
    {
        var diagramsDir = MermaidSourceLoader.ResolveDiagramsDir(env);
        var manifestPath = Path.Combine(diagramsDir, "diagrams.yaml");
        var map = new Dictionary<string, List<DiagramRef>>(StringComparer.Ordinal);

        if (!File.Exists(manifestPath))
            return map;

        var yaml = File.ReadAllText(manifestPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var manifest = deserializer.Deserialize<DiagramManifest>(yaml);

        foreach (var diagram in manifest.Diagrams)
        {
            if (diagram.Tags is not { Count: > 0 })
                continue;

            var mermaid = MermaidSourceLoader.TryRead(diagramsDir, diagram.Source);
            if (mermaid is null) continue;

            var diagramRef = new DiagramRef(diagram.Title, diagram.Description, mermaid);

            foreach (var tag in diagram.Tags)
            {
                if (!map.TryGetValue(tag, out var list))
                {
                    list = [];
                    map[tag] = list;
                }
                list.Add(diagramRef);
            }
        }

        return map;
    }

    private sealed record DiagramRef(string Title, string? Description, string MermaidSource);

    private sealed class DiagramManifest
    {
        public List<DiagramEntry> Diagrams { get; set; } = [];
    }

    private sealed class DiagramEntry
    {
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public List<string>? Tags { get; set; }
        public string? Auto { get; set; }
        public string? Module { get; set; }
    }

    private sealed class TagNameComparer : IEqualityComparer<OpenApiTag>
    {
        public static readonly TagNameComparer Instance = new();

        public bool Equals(OpenApiTag? x, OpenApiTag? y) =>
            string.Equals(x?.Name, y?.Name, StringComparison.Ordinal);

        public int GetHashCode(OpenApiTag obj) =>
            obj.Name?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
