<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import {
        ArrowRight,
        Check,
        Zap,
        Shield,
        Globe,
        Activity,
        Database,
        Code,
        Layers,
        BarChart3,
        Bell,
        Users,
        Lock,
        Server,
    } from "@lucide/svelte";
</script>

<div class="page-wrap">
    <!-- Page heading -->
    <div class="page-hero">
        <div class="page-label">Features</div>
        <h1>
            Everything you need.<br />
            <em>Nothing you don't.</em>
        </h1>
        <p>
            Built on .NET 10 with full Nightscout API compatibility. Every
            feature serves a real-world diabetes management need.
        </p>
    </div>

    <!-- 01 Core Features -->
    <section class="page-section">
        <div class="page-section-head">
            <div class="page-section-label">&#167; 01 &mdash; Core</div>
            <h2>Platform <em>fundamentals.</em></h2>
        </div>

        <div class="grid md:grid-cols-2 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-12 h-12 rounded-lg bg-green-500/15 flex items-center justify-center mb-4">
                    <Check class="w-6 h-6 text-green-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Full Nightscout API Compatibility</h3>
                <p class="text-muted-foreground mb-4">
                    Drop-in replacement with v1, v2, and v3 API support. Your existing
                    apps, uploaders, and integrations work without any changes.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />xDrip+ compatible</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Loop compatible</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />AndroidAPS compatible</li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-12 h-12 rounded-lg bg-blue-500/15 flex items-center justify-center mb-4">
                    <Zap class="w-6 h-6 text-blue-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Modern Performance</h3>
                <p class="text-muted-foreground mb-4">
                    Built on .NET 10 with PostgreSQL for exceptional throughput. Handle
                    years of data without slowdowns.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Sub-millisecond query response</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Efficient memory usage</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Scales with your data</li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-12 h-12 rounded-lg bg-cyan-500/15 flex items-center justify-center mb-4">
                    <Activity class="w-6 h-6 text-cyan-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Real-Time Updates</h3>
                <p class="text-muted-foreground mb-4">
                    WebSocket and SignalR for instant glucose updates. Socket.IO bridge
                    for Nightscout client compatibility.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Instant data sync</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Socket.IO compatibility</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Low bandwidth usage</li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-12 h-12 rounded-lg bg-purple-500/15 flex items-center justify-center mb-4">
                    <BarChart3 class="w-6 h-6 text-purple-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Dashboard &amp; Reports</h3>
                <p class="text-muted-foreground mb-4">
                    Responsive dashboard with AGP, TIR analysis, basal delivery,
                    insulin tracking, and customizable stat widgets.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Ambulatory Glucose Profile</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Time in Range analysis</li>
                    <li class="flex items-center gap-2"><Check class="w-4 h-4 text-green-500" />Customizable widgets</li>
                </ul>
            </div>
        </div>
    </section>

    <!-- 02 Integrations -->
    <section class="page-section">
        <div class="page-section-head">
            <div class="page-section-label">&#167; 02 &mdash; Integrations</div>
            <h2>22 sources. <em>One API surface.</em></h2>
        </div>

        <div class="grid md:grid-cols-3 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Dexcom</h3>
                <p class="text-sm text-muted-foreground">
                    Direct Dexcom Share integration. Automatic data sync from G6, G7,
                    and ONE sensors.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">FreeStyle Libre</h3>
                <p class="text-sm text-muted-foreground">
                    Connect via LibreView for Libre 2 and 3. Real-time sync through
                    LibreLinkUp.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Glooko</h3>
                <p class="text-sm text-muted-foreground">
                    Import from Glooko for comprehensive diabetes management data
                    aggregation.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Nightscout</h3>
                <p class="text-sm text-muted-foreground">
                    Run as a proxy alongside existing Nightscout, or migrate your
                    complete data history.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">xDrip+</h3>
                <p class="text-sm text-muted-foreground">
                    Full xDrip+ uploader support. Keep using your favorite Android CGM
                    app as-is.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">More every release</h3>
                <p class="text-sm text-muted-foreground">
                    Extensible connector architecture makes adding new data sources
                    straightforward. Requests welcome.
                </p>
            </div>
        </div>
    </section>

    <!-- 03 Security -->
    <section class="page-section">
        <div class="page-section-head">
            <div class="page-section-label">&#167; 03 &mdash; Security</div>
            <h2>Your data stays on <em>your server.</em></h2>
        </div>

        <div class="grid md:grid-cols-2 lg:grid-cols-3 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3">
                    <Server class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Self-Hosted</h3>
                <p class="text-sm text-muted-foreground">
                    No cloud dependencies. No third-party data access. Your health
                    data never leaves your infrastructure.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3">
                    <Lock class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Role-Based Access</h3>
                <p class="text-sm text-muted-foreground">
                    Fine-grained permissions for caregivers, healthcare providers, and
                    family members.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3">
                    <Users class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Multi-User &amp; Multitenant</h3>
                <p class="text-sm text-muted-foreground">
                    Row-level security isolates every tenant. Run one install for a
                    household, clinic, or community.
                </p>
            </div>
        </div>
    </section>

    <!-- 04 Developer -->
    <section class="page-section">
        <div class="page-section-head">
            <div class="page-section-label">&#167; 04 &mdash; Developer</div>
            <h2>Open source. <em>Easy to extend.</em></h2>
        </div>

        <div class="grid md:grid-cols-2 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">OpenAPI Documentation</h3>
                <p class="text-sm text-muted-foreground">
                    Full OpenAPI/Swagger documentation with Scalar UI. Generate client
                    libraries for any language.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Aspire Dashboard</h3>
                <p class="text-sm text-muted-foreground">
                    Built-in observability with distributed tracing, structured logging,
                    and metrics visualization.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Clean Architecture</h3>
                <p class="text-sm text-muted-foreground">
                    Well-organized codebase following Clean Architecture principles.
                    Easy to extend and customize.
                </p>
            </div>
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Docker Ready</h3>
                <p class="text-sm text-muted-foreground">
                    Production-ready images with docker-compose configurations. Deploy
                    anywhere containers run.
                </p>
            </div>
        </div>
    </section>

    <!-- CTA -->
    <section class="page-cta">
        <div class="page-cta-inner">
            <div class="page-section-label">Get started</div>
            <h2>Ready to take control?</h2>
            <p>
                Set up your own Nocturne instance in minutes. Free, open source,
                and always will be.
            </p>
            <div class="flex flex-col sm:flex-row gap-4">
                <Button href="/docs/installation" size="lg" class="gap-2">
                    Installation guide <ArrowRight class="w-4 h-4" />
                </Button>
                <Button href="/docs" variant="outline" size="lg">
                    Read the docs
                </Button>
            </div>
        </div>
    </section>
</div>

<style>
    .page-wrap {
        max-width: 1200px;
        margin: 0 auto;
        padding: 0 24px;
    }

    .page-hero {
        padding: 80px 0 60px;
        border-bottom: 1px solid var(--border);
        margin-bottom: 0;
    }

    .page-label {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted-foreground);
        margin-bottom: 16px;
    }

    .page-hero h1 {
        font-size: clamp(2rem, 4vw, 3.2rem);
        font-weight: 700;
        line-height: 1.15;
        letter-spacing: -0.025em;
        color: var(--foreground);
        margin: 0 0 16px;
    }

    .page-hero h1 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .page-hero p {
        font-size: 1rem;
        line-height: 1.65;
        color: var(--muted-foreground);
        max-width: 560px;
        margin: 0;
    }

    .page-section {
        padding: 80px 0;
        border-top: 1px solid var(--border);
    }

    .page-section-head {
        margin-bottom: 48px;
    }

    .page-section-label {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted-foreground);
        margin-bottom: 14px;
    }

    .page-section-head h2 {
        font-size: clamp(1.5rem, 3vw, 2.2rem);
        font-weight: 700;
        line-height: 1.2;
        letter-spacing: -0.02em;
        color: var(--foreground);
        margin: 0;
    }

    .page-section-head h2 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .page-cta {
        border-top: 1px solid var(--border);
        padding: 80px 0;
    }

    .page-cta-inner {
        max-width: 560px;
    }

    .page-cta-inner h2 {
        font-size: clamp(1.5rem, 3vw, 2.2rem);
        font-weight: 700;
        letter-spacing: -0.02em;
        color: var(--foreground);
        margin: 0 0 12px;
    }

    .page-cta-inner p {
        font-size: 0.9375rem;
        line-height: 1.65;
        color: var(--muted-foreground);
        margin: 0 0 28px;
    }
</style>
