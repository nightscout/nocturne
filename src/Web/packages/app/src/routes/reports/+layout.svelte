<script lang="ts">
  import { page } from "$app/state";
  import Button from "$lib/components/ui/button/button.svelte";
  import { ReportsFilterSidebar } from "$lib/components/layout";
  import { ArrowLeftIcon, Filter, Calendar } from "lucide-svelte";
  import { queryParam } from "sveltekit-search-params";

  let { children, data } = $props();

  // Filter sidebar state
  let filterSidebarOpen = $state(false);

  // URL search params for display
  const days = queryParam("days", {
    encode: (value: number | undefined) => value?.toString() ?? "",
    decode: (value: string | null) => {
      if (!value) return undefined;
      const parsed = parseInt(value);
      return isNaN(parsed) ? undefined : parsed;
    },
    defaultValue: undefined,
  });

  const fromDate = queryParam("from", {
    encode: (value: string | undefined) => value ?? "",
    decode: (value: string | null) => value || undefined,
    defaultValue: undefined,
  });

  const toDate = queryParam("to", {
    encode: (value: string | undefined) => value ?? "",
    decode: (value: string | null) => value || undefined,
    defaultValue: undefined,
  });

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
    if ($days) {
      if ($days === 1) return "Today";
      return `Last ${$days} days`;
    }
    if ($fromDate && $toDate) {
      return `${$fromDate} to ${$toDate}`;
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
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
</svelte:head>

<div class="min-h-full bg-background">
  {#if page.url.pathname !== "/reports"}
    <!-- Report Header -->
    <div
      class="sticky top-14 z-10 border-b border-border bg-card/95 backdrop-blur supports-[backdrop-filter]:bg-card/60"
    >
      <div class="px-6 py-3">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-4">
            <Button href="/reports" variant="ghost" size="icon">
              <ArrowLeftIcon class="w-4 h-4" />
            </Button>
            <div>
              <h1 class="text-xl font-bold text-foreground">{reportName}</h1>
              <div
                class="flex items-center gap-2 text-sm text-muted-foreground"
              >
                <Calendar class="h-3 w-3" />
                <span>{dateRangeDisplay()}</span>
                <span>•</span>
                <span>{data.entries.length} entries</span>
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
              <Filter class="w-4 h-4" />
              <span class="hidden sm:inline">Filters</span>
            </Button>
          {/if}
        </div>
      </div>
    </div>
  {/if}

  <!-- Main Content -->
  <main class="px-6 py-6">
    {@render children()}
  </main>

  <!-- Filter Sidebar -->
  <ReportsFilterSidebar
    bind:open={filterSidebarOpen}
    onOpenChange={(open) => (filterSidebarOpen = open)}
  />
</div>
