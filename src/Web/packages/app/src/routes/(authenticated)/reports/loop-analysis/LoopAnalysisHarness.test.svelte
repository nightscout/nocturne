<!--
  A Loop Analysis page inside its layout, with stand-ins for what the reports layout shows of it:
  the page switcher in the header and the Filters panel.
-->
<script lang="ts">
  import type { Component } from "svelte";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { createReportSlots } from "$lib/hooks/report-slots.svelte";
  import ReportPageSwitcher from "$lib/components/reports/ReportPageSwitcher.svelte";
  import { loopAnalysisHeaderMark } from "$lib/components/reports/loop-analysis/loop-analysis-tour.svelte";
  import Layout from "./+layout.svelte";

  let { report }: { report: Component } = $props();
  const Report = $derived(report);

  const slots = createReportSlots();

  // The panel's own navigation, which loop-analysis-filters-panel.svelte.test.ts runs for real.
  function write(part: ((search: URLSearchParams) => URLSearchParams) | undefined) {
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- a query on the page on screen
    if (part) void goto(`${page.url.pathname}?${part(page.url.searchParams).toString()}`);
  }
</script>

<header {@attach loopAnalysisHeaderMark()}><ReportPageSwitcher /></header>

<Layout>
  <Report />
</Layout>

{#if slots.filters}
  <section aria-label="Filters panel">
    <button type="button" onclick={() => slots.filters?.begin()}>Open filters</button>
    {@render slots.filters.fields()}
    <button type="button" onclick={() => write(slots.filters?.apply)}>Apply filters</button>
    <button type="button" onclick={() => write(slots.filters?.reset)}>Reset filters</button>
  </section>
{/if}
