<script lang="ts">
    import { Key } from "@nocturne/ui/keys";
    import {page} from "$app/state";
    import {formatMediumDateRange} from "#lib/utils/formatting.js";
    import { Button } from "#lib/components/ui/button/index.js";
    import {ReportsFilterSidebar} from "#lib/components/layout/index.js";
    import ResourceGuard from "#lib/components/reports/ResourceGuard.svelte";
    import ReportPageSwitcher from "#lib/components/reports/ReportPageSwitcher.svelte";
    import { loopAnalysisHeaderMark } from "#lib/components/reports/loop-analysis/loop-analysis-tour.svelte.js";
    import HistoryLimitNotice from "#lib/components/layout/HistoryLimitNotice.svelte";
    import ReportPrintHeader from "#lib/components/reports/print/ReportPrintHeader.svelte";
    import PrintReportButton from "#lib/components/reports/print/PrintReportButton.svelte";
    import {
        createReportPrintContext,
        installPrintFitFallback,
        printReport,
    } from "#lib/components/reports/print/report-print.svelte.js";
    import {reportCategories, reportDescription, reportSubRouteTitle, reportTitle} from "#lib/navigation/report-navigation.svelte.js";
    import Filter from "@lucide/svelte/icons/funnel";
    import Calendar from "@lucide/svelte/icons/calendar";
    import ChevronDown from "@lucide/svelte/icons/chevron-down";
    import ReportHeaderWashDeferred from "#lib/components/reports/ReportHeaderWashDeferred.svelte";
    import { toast } from "svelte-sonner";
    import { onMount } from "svelte";
    import {useDateParams, setDateParamsContext, createSharedRangeUse} from "#lib/hooks/date-params.svelte.js";
    import {createResourceContext} from "#lib/hooks/resource-context.svelte.js";
    import {createReportSlots} from "#lib/hooks/report-slots.svelte.js";

    let {children} = $props();

    // Filter sidebar state
    let filterSidebarOpen = $state(false);

    // Create shared date params instance and provide via context
    // This is the SINGLE source of truth for all report components.
    // Baseline default is 14 days — the clinical standard for glucose reports
    // (AGP, overview, executive summary). Reports that prefer a shorter/longer
    // window (e.g. readings=7, insulin-delivery=30) adjust via
    // requireDateParamsContext(N); the headline 14-day reports then need no
    // adjustment, so they no longer depend on the adjust-up path resolving.
    const params = useDateParams(14);
    setDateParamsContext(params);

    // Reports declare whether they read the shared range; those that carry their
    // own period controls (comparison, year overview, day in review, data quality)
    // do not, and the range control is hidden for them rather than sitting there
    // doing nothing.
    const sharedRangeUse = createSharedRangeUse();

    // Create resource context for layout-level loading/error handling
    const resourceCtx = createResourceContext();

    // Filters a report adds to the Filters panel, below the date range.
    createReportSlots();

    // Whether to use the ResourceGuard (skip for main reports page which has custom design)
    const useResourceGuard = $derived(page.url.pathname !== "/reports");

    const printCtx = createReportPrintContext();

    /** Resolves true once the print dialog has run, false when it never opened. */
    async function handlePrint(): Promise<boolean> {
        if (printCtx.printing) return false;
        printCtx.printing = true;
        const url = page.url.href;
        const preparationVersion = printCtx.preparationVersion;
        try {
            await printReport(
                () => printCtx.prepare(),
                () => page.url.href === url && printCtx.preparationVersion === preparationVersion,
            );
            return true;
        } catch (error) {
            console.error("Failed to prepare report for printing:", error);
            toast.error("Could not load the complete report. Try again before printing.");
            return false;
        } finally {
            printCtx.printing = false;
        }
    }

    onMount(() => {
        const keydown = (event: KeyboardEvent) => {
            if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === Key.P) {
                event.preventDefault();
                void handlePrint();
            }
        };
        window.addEventListener("keydown", keydown);
        return () => window.removeEventListener("keydown", keydown);
    });

    let reportRoot = $state<HTMLElement | null>(null);
    $effect(() => installPrintFitFallback(() => reportRoot));

    const registryTitles = $derived(
        new Map(reportCategories().flatMap((c) => c.reports.map((r) => [r.href, reportTitle(c, r)])))
    );

    // Extract report name from the URL
    const reportName = $derived.by(() => {
        const declared = registryTitles.get(page.url.pathname) ?? reportSubRouteTitle(page.url.pathname);
        if (declared) return declared;

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

    const description = $derived(reportDescription(page.url.pathname));

    // Show the range control only where the report actually reads the range
    const showFilters = $derived(
        page.url.pathname !== "/reports" && sharedRangeUse.consumed
    );

    const printPeriod = $derived(
        printCtx.meta.period ??
            (sharedRangeUse.consumed ? {from: params.fromDay, to: params.toDay} : undefined)
    );

    // Format date range for display
    // Read from the resolved range, not the URL: a default range seeded during
    // hydration never reaches the URL, which would otherwise read as no range.
    const dateRangeDisplay = $derived.by(() => {
        const {days} = params.dateRangeInput;
        if (days) return days === 1 ? "Today" : `Last ${days} days`;
        return formatMediumDateRange(params.startDate, params.endDate);
    });
</script>

<svelte:head>
    <title>{reportName} - Nocturne</title>
    {#if description}
        <meta name="description" content={description}/>
    {/if}
    <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
</svelte:head>

<!-- --headroom-top sits a Headroom panel flush under the sticky report header (h-14 plus its border). -->
<div class="@container relative min-h-full bg-background [--headroom-top:calc(var(--app-sticky-top,0px)+3.5rem+1px)]" bind:this={reportRoot} data-report-root>
    {#if page.url.pathname !== "/reports"}
        <ReportPrintHeader title={reportName} period={printPeriod} />

        <div
                class="sticky top-(--app-sticky-top,0px) z-20 transition-all duration-300 border-b border-border bg-card/95 backdrop-blur supports-backdrop-filter:bg-card/60 print:hidden"
                {@attach loopAnalysisHeaderMark()}
        >
            <!-- At 35% the darkest wash keeps the bar's text above 9:1 in either theme. -->
            <ReportHeaderWashDeferred name={page.url.pathname} class="inset-0 opacity-35"/>
            <div class="relative flex h-14 items-center justify-between gap-2 px-3 @md:px-6">
                <!-- Report info -->
                <div class="flex min-w-0 items-center gap-3">
                    <h1 class="truncate text-lg font-semibold text-foreground">{reportName}</h1>
                    {#if showFilters}
                        <Button
                                variant="outline"
                                size="sm"
                                class="hidden shrink-0 @xl:inline-flex"
                                onclick={() => (filterSidebarOpen = true)}
                                aria-label="Change date range"
                        >
                            <Calendar class="h-3.5 w-3.5"/>
                            <span>{dateRangeDisplay}</span>
                            <ChevronDown class="h-3 w-3 opacity-60"/>
                        </Button>
                    {/if}
                </div>

                <div class="flex shrink-0 items-center gap-2">
                    <PrintReportButton print={handlePrint} disabled={printCtx.printing}/>
                    {#if showFilters}
                        <Button
                                variant="outline"
                                size="sm"
                                onclick={() => (filterSidebarOpen = true)}
                                aria-label="Filters"
                        >
                            <Filter class="w-4 h-4"/>
                            <span class="hidden @lg:inline">Filters</span>
                        </Button>
                    {/if}
                </div>
            </div>
            <ReportPageSwitcher />
        </div>
    {/if}

    <HistoryLimitNotice class="mx-3 mt-3 w-auto @md:mx-6 print:hidden" />

    <!-- Main Content -->
    <div class={useResourceGuard ? "relative mx-auto w-full max-w-7xl p-3 @md:p-6 print:max-w-none print:p-3" : "relative"}>
        {#if useResourceGuard}
            <ResourceGuard
                loading={resourceCtx.loading}
                error={resourceCtx.error}
                hasData={resourceCtx.hasData}
                refreshing={resourceCtx.refreshing}
                errorTitle={resourceCtx.errorTitle}
                onRetry={resourceCtx.refetch}
            >
                {@render children()}
            </ResourceGuard>
        {:else}
            {@render children()}
        {/if}
    </div>

    <!-- Filter Sidebar -->
    {#if showFilters}
        <ReportsFilterSidebar
                bind:open={filterSidebarOpen}
                onOpenChange={(open) => (filterSidebarOpen = open)}
        />
    {/if}
</div>
