<script lang="ts">
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { DEFAULT_TOP_WIDGETS } from "$lib/types/dashboard-widgets";
  import type { Component } from "svelte";

  interface Props {
    /** Ordered list of widget IDs to display */
    widgets?: WidgetId[];
    /** Maximum number of widgets to show (default 3) */
    maxWidgets?: number;
  }

  let { widgets = DEFAULT_TOP_WIDGETS, maxWidgets = 3 }: Props = $props();

  // Limit to max widgets
  const displayWidgets = $derived(widgets.slice(0, maxWidgets));

  // Lazy loaders — only the rendered widgets are imported
  const widgetLoaders: Partial<Record<WidgetId, () => Promise<{ default: Component }>>> = {
    [WidgetId.BgDelta]: () => import("./widgets/BgDeltaWidget.svelte"),
    [WidgetId.LastUpdated]: () => import("./widgets/LastUpdatedWidget.svelte"),
    [WidgetId.ConnectionStatus]: () => import("./widgets/ConnectionStatusWidget.svelte"),
    [WidgetId.Meals]: () => import("./widgets/MealsWidget.svelte"),
    [WidgetId.Trackers]: () => import("./widgets/TrackersWidget.svelte"),
    [WidgetId.TirChart]: () => import("./widgets/TirChartWidget.svelte"),
    [WidgetId.DailySummary]: () => import("./widgets/DailySummaryWidget.svelte"),
    [WidgetId.Clock]: () => import("./widgets/ClockWidget.svelte"),
    [WidgetId.Tdd]: () => import("./widgets/TddWidget.svelte"),
  };

  // Cache resolved modules so re-renders don't re-import
  const cache = new Map<WidgetId, Promise<Component>>();
  function loadWidget(id: WidgetId): Promise<Component> | undefined {
    if (!cache.has(id)) {
      const loader = widgetLoaders[id];
      if (loader) cache.set(id, loader().then((m) => m.default));
    }
    return cache.get(id);
  }
</script>

<div class="@container grid grid-cols-1 @md:grid-cols-3 gap-2 @md:gap-4">
  {#each displayWidgets as widgetId (widgetId)}
    {@const promise = loadWidget(widgetId)}
    {#if promise}
      {#await promise then WidgetComponent}
        <WidgetComponent />
      {/await}
    {/if}
  {/each}
</div>
