<!--
  Stand-in for the reports Filters sheet, which the page tests do not drive: the sheet is built on
  the shared range, which they fix. It runs the filters the page registers with the layout.
-->
<script lang="ts">
  import { page, type ReadonlyURLSearchParams } from "$app/state";
  import { goto } from "$app/navigation";
  import { getReportSlots } from "#lib/hooks/report-slots.svelte.js";

  const slots = getReportSlots();

  // The panel's own navigation, which loop-analysis-filters-panel.svelte.test.ts runs for real.
  function write(part: ((search: ReadonlyURLSearchParams) => URLSearchParams) | undefined) {
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- a query on the page on screen
    if (part) void goto(`${page.url.pathname}?${part(page.url.searchParams).toString()}`);
  }
</script>

{#if slots?.filters}
  <section aria-label="Filters panel">
    <button type="button" onclick={() => slots.filters?.begin()}>Open filters</button>
    {@render slots.filters.fields()}
    <button type="button" onclick={() => write(slots.filters?.apply)}>Apply filters</button>
    <button type="button" onclick={() => write(slots.filters?.reset)}>Reset filters</button>
  </section>
{/if}
