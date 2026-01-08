<script lang="ts">
    import { Button } from "@nocturne/app/ui/button";
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
        RefreshCw,
        Clock,
        BarChart3,
        Bell,
        Users,
        Lock,
        Server,
    } from "@lucide/svelte";
</script>

<div class="container mx-auto px-4 py-12">
    <!-- Hero -->
    <div class="text-center mb-16">
        <h1 class="text-4xl md:text-5xl font-bold tracking-tight mb-4">
            Features
        </h1>
        <p class="text-lg text-muted-foreground max-w-2xl mx-auto">
            Everything you need to manage your diabetes data, built on modern
            technology with full Nightscout compatibility.
        </p>
    </div>

    <!-- Core Features -->
    <section class="mb-20">
        <h2 class="text-2xl font-bold mb-8 flex items-center gap-2">
            <div
                class="w-8 h-8 rounded-lg bg-primary/15 flex items-center justify-center"
            >
                <Layers class="w-4 h-4 text-primary" />
            </div>
            Core Features
        </h2>

        <div class="grid md:grid-cols-2 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-12 h-12 rounded-lg bg-green-500/15 flex items-center justify-center mb-4"
                >
                    <Check class="w-6 h-6 text-green-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">
                    Full Nightscout API Compatibility
                </h3>
                <p class="text-muted-foreground mb-4">
                    Drop-in replacement for Nightscout with v1, v2, and v3 API support.
                    Your existing apps, uploaders, and integrations work without any
                    changes.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        xDrip+ compatible
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Loop compatible
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        AndroidAPS compatible
                    </li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-12 h-12 rounded-lg bg-blue-500/15 flex items-center justify-center mb-4"
                >
                    <Zap class="w-6 h-6 text-blue-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Modern Performance</h3>
                <p class="text-muted-foreground mb-4">
                    Built on .NET 10 with PostgreSQL for exceptional performance. Handle
                    years of data without slowdowns.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Sub-millisecond query response
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Efficient memory usage
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Scales with your data
                    </li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-12 h-12 rounded-lg bg-cyan-500/15 flex items-center justify-center mb-4"
                >
                    <Activity class="w-6 h-6 text-cyan-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Real-Time Updates</h3>
                <p class="text-muted-foreground mb-4">
                    WebSocket and SignalR support for instant glucose updates. Socket.IO
                    bridge for Nightscout client compatibility.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Instant data sync
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Socket.IO compatibility
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Low bandwidth usage
                    </li>
                </ul>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-12 h-12 rounded-lg bg-purple-500/15 flex items-center justify-center mb-4"
                >
                    <BarChart3 class="w-6 h-6 text-purple-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">
                    Modern Dashboard & Reports
                </h3>
                <p class="text-muted-foreground mb-4">
                    Beautiful, responsive dashboard built with SvelteKit. Advanced
                    reporting with AGP, TIR, and more.
                </p>
                <ul class="space-y-2 text-sm text-muted-foreground">
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Ambulatory Glucose Profile
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Time in Range analysis
                    </li>
                    <li class="flex items-center gap-2">
                        <Check class="w-4 h-4 text-green-500" />
                        Customizable widgets
                    </li>
                </ul>
            </div>
        </div>
    </section>

    <!-- Data Integrations -->
    <section class="mb-20">
        <h2 class="text-2xl font-bold mb-8 flex items-center gap-2">
            <div
                class="w-8 h-8 rounded-lg bg-orange-500/15 flex items-center justify-center"
            >
                <Globe class="w-4 h-4 text-orange-500" />
            </div>
            Data Integrations
        </h2>

        <div class="grid md:grid-cols-3 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Dexcom</h3>
                <p class="text-sm text-muted-foreground">
                    Direct integration with Dexcom Share. Automatic data sync from G6, G7,
                    and ONE sensors.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Libre</h3>
                <p class="text-sm text-muted-foreground">
                    Connect to LibreView for Freestyle Libre 2 and 3 data. Real-time sync
                    with LibreLinkUp.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Glooko</h3>
                <p class="text-sm text-muted-foreground">
                    Import data from Glooko for comprehensive diabetes management data
                    aggregation.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">Nightscout</h3>
                <p class="text-sm text-muted-foreground">
                    Run as a proxy alongside existing Nightscout or migrate your complete
                    data history.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">xDrip+</h3>
                <p class="text-sm text-muted-foreground">
                    Full xDrip+ uploader support. Continue using your favorite Android CGM
                    app.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="text-lg font-semibold mb-2">More Coming</h3>
                <p class="text-sm text-muted-foreground">
                    Extensible connector architecture makes adding new data sources
                    straightforward.
                </p>
            </div>
        </div>
    </section>

    <!-- Security & Privacy -->
    <section class="mb-20">
        <h2 class="text-2xl font-bold mb-8 flex items-center gap-2">
            <div
                class="w-8 h-8 rounded-lg bg-red-500/15 flex items-center justify-center"
            >
                <Shield class="w-4 h-4 text-red-500" />
            </div>
            Security & Privacy
        </h2>

        <div class="grid md:grid-cols-2 lg:grid-cols-3 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3"
                >
                    <Server class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Self-Hosted</h3>
                <p class="text-sm text-muted-foreground">
                    Your data stays on your server. No cloud dependencies or third-party
                    data access.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3"
                >
                    <Lock class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Role-Based Access</h3>
                <p class="text-sm text-muted-foreground">
                    Fine-grained permissions for caregivers, healthcare providers, and
                    family members.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <div
                    class="w-10 h-10 rounded-lg bg-red-500/15 flex items-center justify-center mb-3"
                >
                    <Users class="w-5 h-5 text-red-500" />
                </div>
                <h3 class="font-semibold mb-2">Multi-User Support</h3>
                <p class="text-sm text-muted-foreground">
                    Manage multiple profiles for families. Each person gets their own data
                    and settings.
                </p>
            </div>
        </div>
    </section>

    <!-- Developer Features -->
    <section class="mb-20">
        <h2 class="text-2xl font-bold mb-8 flex items-center gap-2">
            <div
                class="w-8 h-8 rounded-lg bg-amber-500/15 flex items-center justify-center"
            >
                <Code class="w-4 h-4 text-amber-500" />
            </div>
            Developer Features
        </h2>

        <div class="grid md:grid-cols-2 gap-6">
            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">OpenAPI Documentation</h3>
                <p class="text-sm text-muted-foreground mb-3">
                    Full OpenAPI/Swagger documentation with Scalar UI. Generate client
                    libraries for any language.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Aspire Dashboard</h3>
                <p class="text-sm text-muted-foreground mb-3">
                    Built-in observability with distributed tracing, structured logging,
                    and metrics visualization.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Clean Architecture</h3>
                <p class="text-sm text-muted-foreground mb-3">
                    Well-organized codebase following Clean Architecture principles.
                    Easy to extend and customize.
                </p>
            </div>

            <div class="p-6 rounded-xl border border-border/60 bg-card/50">
                <h3 class="font-semibold mb-2">Docker Ready</h3>
                <p class="text-sm text-muted-foreground mb-3">
                    Production-ready Docker images with docker-compose configurations.
                    Deploy anywhere containers run.
                </p>
            </div>
        </div>
    </section>

    <!-- CTA -->
    <section class="text-center py-12">
        <div
            class="max-w-xl mx-auto rounded-2xl border border-primary/20 bg-card/80 p-8"
        >
            <h2 class="text-2xl font-bold mb-4">Ready to Get Started?</h2>
            <p class="text-muted-foreground mb-6">
                Set up your own Nocturne instance in minutes with our configuration
                wizard.
            </p>
            <div class="flex flex-col sm:flex-row gap-4 justify-center">
                <Button href="/setup" size="lg" class="gap-2">
                    Start Configuration
                    <ArrowRight class="w-4 h-4" />
                </Button>
                <Button href="/docs" variant="outline" size="lg">
                    Read the Docs
                </Button>
            </div>
        </div>
    </section>
</div>
