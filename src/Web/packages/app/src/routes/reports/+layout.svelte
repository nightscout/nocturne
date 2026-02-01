<script lang="ts">
    import {page} from "$app/state";
    import Button from "$lib/components/ui/button/button.svelte";
    import {ReportsFilterSidebar} from "$lib/components/layout";
    import ResourceGuard from "$lib/components/reports/ResourceGuard.svelte";
    import {Filter, Calendar} from "lucide-svelte";
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
    const dateRangeDisplay = $derived(() => {
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
        <!-- Report Header - unified sticky header with sidebar trigger -->
        <!-- On mobile (md:hidden), position below the MobileHeader with top-14 -->
        <!-- On desktop (md:top-0), position at top since main header is hidden for reports -->
        <div
                class="sticky top-14 md:top-0 z-20 border-b border-border bg-card/95 backdrop-blur supports-backdrop-filter:bg-card/60"
        >
            <div class="flex h-14 items-center justify-between gap-2 px-4">
                <div class="flex items-center gap-2">
                    <!-- Report info -->
                    <div class="flex items-center gap-3">
                        <h1 class="text-lg font-semibold text-foreground">{reportName}</h1>
                        <div
                                class="hidden sm:flex items-center gap-2 text-sm text-muted-foreground"
                        >
                            <Calendar class="h-3 w-3"/>
                            <span>{dateRangeDisplay()}</span>
                        </div>
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
    <main class="relative px-6 py-6">
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
