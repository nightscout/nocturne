<script lang="ts">
  import type { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import {
    DEFAULT_TOP_WIDGETS,
    knownTopWidgets,
    loadTopWidget,
  } from "./widget-registry";
  import WidgetCard from "./widgets/WidgetCard.svelte";

  interface Props {
    /** Ordered list of widget IDs to display */
    widgets?: WidgetId[];
    /** Maximum number of widgets to show (default 3) */
    maxWidgets?: number;
  }

  let { widgets = DEFAULT_TOP_WIDGETS, maxWidgets = 3 }: Props = $props();

  const displayWidgets = $derived(knownTopWidgets(widgets).slice(0, maxWidgets));
</script>

<div class="@container grid grid-cols-1 @md:grid-cols-3 gap-2 @md:gap-4">
  {#each displayWidgets as widgetId (widgetId)}
    {#await loadTopWidget(widgetId)}
      <WidgetCard title="Loading">
        <div class="bg-muted h-7 w-20 animate-pulse rounded"></div>
      </WidgetCard>
    {:then WidgetComponent}
      <WidgetComponent />
    {:catch}
      <!-- A dynamic import fails on a stale chunk after a deploy. Without a
           catch the slot renders nothing, forever, with no trace of why. -->
      <WidgetCard title="Widget unavailable">
        <p class="text-muted-foreground text-xs">
          This widget couldn't be loaded. Reload the page to try again.
        </p>
      </WidgetCard>
    {/await}
  {/each}
</div>
