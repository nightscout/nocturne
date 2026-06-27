<script lang="ts">
    import {page} from "$app/state";
    import { Button } from "$lib/components/ui/button";
    import {ReportsFilterSidebar} from "$lib/components/layout";
    import ResourceGuard from "$lib/components/reports/ResourceGuard.svelte";
    import {Filter, Calendar, ChevronDown} from "lucide-svelte";
    import {useDateParams, setDateParamsContext} from "$lib/hooks/date-params.svelte";
    import {createResourceContext} from "$lib/hooks/resource-context.svelte";

    let {children} = $props();

    // Filter sidebar state
    let filterSidebarOpen = $state(false);

    // Create shared date params instance and provide via context
    // This is the SINGLE source of truth for all report components
    const params = useDateParams();
    setDateParamsContext(params);

    // Create resource context for layout-level loading/error handling
    const resourceCtx = createResourceContext();

    // Whether to use the ResourceGuard (skip for main reports page which has custom design)
    const useResourceGuard = $derived(page.url.pathname !== "/reports");

    // Extract report name from the URL
    const reportName = $derived.by(() => {
        const pathSegments = page.url.pathname.split("/");
        const reportSegment = pathSegments[pathSegments.length - 1];

        if (!reportSegment || reportSegment === "reports") {
            return "Reports";
        }

        // Convert kebab-case to title case
        return (
            reportSegment
                .split("-")
                .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
                .join(" ") + " Report"
        );
    });

    // Determine if we should show the filter button (not on main reports page)
    const showFilters = $derived(page.url.pathname !== "/reports");

    // Format date range for display
    const dateRangeDisplay = $derived.by(() => {
        if (params.days) {
            if (params.days === 1) return "Today";
            return `Last ${params.days} days`;
        }
        if (params.from && params.to) {
            return `${params.from} to ${params.to}`;
        }
        return "Last 7 days";
    });
</script>

<svelte:head>
    <title>{reportName} - Nightscout</title>
    <meta
            name="description"
            content="Nightscout {reportName.toLowerCase()} with comprehensive data analysis and filtering capabilities"
    />
    <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
</svelte:head>

<div class="relative min-h-full bg-background">
    {#if page.url.pathname !== "/reports"}
        <!-- Print-only report header: gives the printed page the context the
             interactive sticky header (hidden below) carries on screen. -->
        <div class="hidden print:block border-b border-border pb-3 mb-4 px-3">
            <h1 class="text-xl font-bold text-foreground">{reportName}</h1>
            <p class="text-sm text-muted-foreground">
                {dateRangeDisplay} · Generated {new Date().toLocaleString()}
            </p>
        </div>

        <!-- Report Header - unified sticky header with sidebar trigger -->
        <!-- On mobile (md:hidden), position below the MobileHeader with top-14 -->
        <!-- On desktop (md:top-0), position at top since main header is hidden for reports -->
        <div
                class="sticky top-14 md:top-0 z-20 border-b border-border bg-card/95 backdrop-blur supports-backdrop-filter:bg-card/60 print:hidden"
        >
            <div class="flex h-14 items-center justify-between gap-2 px-3 @md:px-6">
                <div class="flex items-center gap-2">
                    <!-- Report info -->
                    <div class="flex items-center gap-3">
                        <h1 class="text-lg font-semibold text-foreground">{reportName}</h1>
                        {#if showFilters}
                            <button
                                    type="button"
                                    onclick={() => (filterSidebarOpen = true)}
                                    class="hidden sm:flex items-center gap-1.5 rounded-md border border-border bg-background px-2.5 py-1 text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
                                    aria-label="Change date range"
                            >
                                <Calendar class="h-3.5 w-3.5"/>
                                <span>{dateRangeDisplay}</span>
                                <ChevronDown class="h-3 w-3 opacity-60"/>
                            </button>
                        {/if}
                    </div>
                </div>

                {#if showFilters}
                    <Button
                            variant="outline"
                            size="sm"
                            onclick={() => (filterSidebarOpen = true)}
                            class="gap-2"
                    >
                        <Filter class="w-4 h-4"/>
                        <span class="hidden sm:inline">Filters</span>
                    </Button>
                {/if}
            </div>
        </div>
    {/if}

    <!-- Main Content -->
    <main class="relative">
        {#if useResourceGuard}
            <ResourceGuard
                loading={resourceCtx.loading}
                error={resourceCtx.error}
                hasData={resourceCtx.hasData}
                errorTitle={resourceCtx.errorTitle}
                onRetry={resourceCtx.refetch}
            >
                {@render children()}
            </ResourceGuard>
        {:else}
            {@render children()}
        {/if}
    </main>

    <!-- Filter Sidebar -->
    <ReportsFilterSidebar
            bind:open={filterSidebarOpen}
            onOpenChange={(open) => (filterSidebarOpen = open)}
    />
</div>
