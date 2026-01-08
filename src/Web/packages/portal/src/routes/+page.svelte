<script lang="ts">
    import { Button } from "@nocturne/app/ui/button";
    import {
        ArrowRight,
        Check,
        Zap,
        Shield,
        RefreshCw,
        Globe,
        Database,
        Activity,
        Settings,
        Download,
        Server,
    } from "@lucide/svelte";
</script>

<!-- Hero Section -->
<section class="relative overflow-hidden">
    <div class="container mx-auto px-4 py-20 md:py-28">
        <div class="max-w-3xl mx-auto text-center">
            <h1
                class="text-4xl md:text-5xl lg:text-6xl font-bold tracking-tight mb-6"
            >
                Your Diabetes Data,
                <span
                    class="bg-gradient-to-r from-primary to-primary/60 bg-clip-text text-transparent"
                >
                    Your Way
                </span>
            </h1>
            <p class="text-lg md:text-xl text-muted-foreground mb-8 max-w-2xl mx-auto">
                A modern, open-source platform for managing your glucose data with full
                Nightscout compatibility. Built by the diabetes community, for the
                diabetes community.
            </p>
            <div class="flex flex-col sm:flex-row gap-4 justify-center">
                <Button href="/setup" size="lg" class="gap-2 text-base">
                    Get Started
                    <ArrowRight class="w-4 h-4" />
                </Button>
                <Button href="/features" variant="outline" size="lg" class="text-base">
                    Explore Features
                </Button>
            </div>
        </div>
    </div>

    <!-- Background gradient effect -->
    <div
        class="absolute inset-0 -z-10 bg-[radial-gradient(ellipse_at_top,_var(--tw-gradient-stops))] from-primary/10 via-transparent to-transparent"
    ></div>
</section>

<!-- Features Grid -->
<section class="py-20 bg-muted/30">
    <div class="container mx-auto px-4">
        <div class="text-center mb-12">
            <h2 class="text-3xl font-bold mb-4">Why Choose Nocturne?</h2>
            <p class="text-muted-foreground max-w-2xl mx-auto">
                Modern architecture meets proven reliability. Get the features you need
                without sacrificing compatibility.
            </p>
        </div>

        <div class="grid md:grid-cols-2 lg:grid-cols-3 gap-6 max-w-5xl mx-auto">
            <!-- Feature Card 1 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-green-500/15 flex items-center justify-center mb-4"
                >
                    <Check class="w-6 h-6 text-green-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Nightscout Compatible</h3>
                <p class="text-sm text-muted-foreground">
                    Full API compatibility means your existing apps and devices work
                    seamlessly. No migration headaches.
                </p>
            </div>

            <!-- Feature Card 2 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-blue-500/15 flex items-center justify-center mb-4"
                >
                    <Zap class="w-6 h-6 text-blue-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Modern Performance</h3>
                <p class="text-sm text-muted-foreground">
                    Built on .NET 10 for blazing fast performance. Handle more data with
                    less resources.
                </p>
            </div>

            <!-- Feature Card 3 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-purple-500/15 flex items-center justify-center mb-4"
                >
                    <Shield class="w-6 h-6 text-purple-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Self-Hosted Security</h3>
                <p class="text-sm text-muted-foreground">
                    Your data stays on your server. Full control over your health
                    information, always.
                </p>
            </div>

            <!-- Feature Card 4 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-orange-500/15 flex items-center justify-center mb-4"
                >
                    <Globe class="w-6 h-6 text-orange-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Multiple Data Sources</h3>
                <p class="text-sm text-muted-foreground">
                    Connect Dexcom, Libre, Glooko, and more. Bring all your data
                    together in one place.
                </p>
            </div>

            <!-- Feature Card 5 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-cyan-500/15 flex items-center justify-center mb-4"
                >
                    <Activity class="w-6 h-6 text-cyan-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Real-Time Monitoring</h3>
                <p class="text-sm text-muted-foreground">
                    Live glucose updates with WebSocket support. Stay informed with
                    instant notifications.
                </p>
            </div>

            <!-- Feature Card 6 -->
            <div
                class="p-6 rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm"
            >
                <div
                    class="w-12 h-12 rounded-lg bg-amber-500/15 flex items-center justify-center mb-4"
                >
                    <RefreshCw class="w-6 h-6 text-amber-500" />
                </div>
                <h3 class="text-lg font-semibold mb-2">Easy Migration</h3>
                <p class="text-sm text-muted-foreground">
                    Import your existing Nightscout data with one click. Or run both
                    side-by-side to test first.
                </p>
            </div>
        </div>
    </div>
</section>

<!-- How It Works -->
<section class="py-20">
    <div class="container mx-auto px-4">
        <div class="text-center mb-12">
            <h2 class="text-3xl font-bold mb-4">Get Started in Minutes</h2>
            <p class="text-muted-foreground max-w-2xl mx-auto">
                Our configuration wizard generates everything you need for a
                production-ready deployment.
            </p>
        </div>

        <div class="max-w-4xl mx-auto">
            <div class="grid md:grid-cols-3 gap-8">
                <!-- Step 1 -->
                <div class="text-center">
                    <div
                        class="w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center mx-auto mb-4"
                    >
                        <Settings class="w-8 h-8 text-primary" />
                    </div>
                    <div
                        class="text-sm font-medium text-primary mb-2 uppercase tracking-wider"
                    >
                        Step 1
                    </div>
                    <h3 class="text-lg font-semibold mb-2">Configure</h3>
                    <p class="text-sm text-muted-foreground">
                        Choose your setup type and configure your data sources through our
                        friendly wizard.
                    </p>
                </div>

                <!-- Step 2 -->
                <div class="text-center">
                    <div
                        class="w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center mx-auto mb-4"
                    >
                        <Download class="w-8 h-8 text-primary" />
                    </div>
                    <div
                        class="text-sm font-medium text-primary mb-2 uppercase tracking-wider"
                    >
                        Step 2
                    </div>
                    <h3 class="text-lg font-semibold mb-2">Deploy</h3>
                    <p class="text-sm text-muted-foreground">
                        Download your configuration files and deploy with Docker in one
                        command.
                    </p>
                </div>

                <!-- Step 3 -->
                <div class="text-center">
                    <div
                        class="w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center mx-auto mb-4"
                    >
                        <Activity class="w-8 h-8 text-primary" />
                    </div>
                    <div
                        class="text-sm font-medium text-primary mb-2 uppercase tracking-wider"
                    >
                        Step 3
                    </div>
                    <h3 class="text-lg font-semibold mb-2">Monitor</h3>
                    <p class="text-sm text-muted-foreground">
                        Start tracking your glucose data with a modern, responsive
                        interface.
                    </p>
                </div>
            </div>
        </div>
    </div>
</section>

<!-- CTA Section -->
<section class="py-20 bg-primary/5">
    <div class="container mx-auto px-4">
        <div
            class="max-w-2xl mx-auto text-center rounded-2xl border border-primary/20 bg-card/80 backdrop-blur-sm p-8 md:p-12"
        >
            <h2 class="text-2xl md:text-3xl font-bold mb-4">
                Ready to Take Control?
            </h2>
            <p class="text-muted-foreground mb-6">
                Join the community of people managing their diabetes data on their own
                terms. Free, open source, and always will be.
            </p>
            <div class="flex flex-col sm:flex-row gap-4 justify-center">
                <Button href="/setup" size="lg" class="gap-2 text-base">
                    Start Configuration
                    <ArrowRight class="w-4 h-4" />
                </Button>
                <Button href="/docs" variant="outline" size="lg" class="text-base">
                    Read the Docs
                </Button>
            </div>
        </div>
    </div>
</section>
