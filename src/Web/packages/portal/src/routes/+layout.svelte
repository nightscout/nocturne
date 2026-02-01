<script lang="ts">
    import "../app.css";
    import { page } from "$app/state";
    import StepIndicator from "$lib/components/StepIndicator.svelte";
    import SiteHeader from "$lib/components/SiteHeader.svelte";
    import SiteFooter from "$lib/components/SiteFooter.svelte";

    let { children } = $props();

    // Determine current step from URL path and query params
    // Setup page has internal steps: 0 (type selection), 1 (config), 2 (connectors)
    // Map these to indicator steps: 1 (Setup), 2 (Connectors), 3 (Download)
    const currentStep = $derived.by(() => {
        const pathname = page.url.pathname;
        if (pathname.startsWith("/setup")) {
            const stepParam = page.url.searchParams.get("step");
            const step = stepParam ? parseInt(stepParam, 10) : 0;
            // step 0 or 1 = Setup (indicator step 1)
            // step 2 = Connectors (indicator step 2)
            return step >= 2 ? 2 : 1;
        }
        if (pathname.startsWith("/download")) return 3;
        return 0; // Not in wizard
    });

    const isInWizard = $derived(currentStep > 0);

    // Check if we're in the docs section (docs has its own layout)
    const isInDocs = $derived(page.url.pathname.startsWith("/docs"));
</script>

<div
    class="min-h-screen flex flex-col bg-gradient-to-br from-background via-background to-primary/5"
>
    <!-- Use simplified header for wizard, full header for marketing pages -->
    {#if isInWizard}
        <header class="border-b border-border/40 backdrop-blur-sm">
            <div class="container mx-auto px-4 py-4 flex items-center gap-4">
                <a
                    href="/"
                    class="flex items-center gap-2 hover:opacity-80 transition-opacity"
                >
                    <div
                        class="w-8 h-8 rounded-lg bg-primary/20 flex items-center justify-center"
                    >
                        <span class="text-primary font-bold">N</span>
                    </div>
                    <span class="text-xl font-semibold">Nocturne Portal</span>
                </a>
            </div>
        </header>

        <div class="border-b border-border/40 bg-card/30 backdrop-blur-sm">
            <div class="container mx-auto px-4 py-6">
                <div class="max-w-xl mx-auto">
                    <StepIndicator {currentStep} />
                </div>
            </div>
        </div>
    {:else}
        <SiteHeader />
    {/if}

    <main class="flex-1 {isInWizard ? 'container mx-auto px-4 py-8' : ''}">
        {@render children()}
    </main>

    {#if isInWizard}
        <footer class="border-t border-border/40 mt-auto">
            <div
                class="container mx-auto px-4 py-6 text-center text-muted-foreground text-sm"
            >
                Nocturne &copy; {new Date().getFullYear()} - Open source diabetes management
            </div>
        </footer>
    {:else}
        <SiteFooter />
    {/if}
</div>
