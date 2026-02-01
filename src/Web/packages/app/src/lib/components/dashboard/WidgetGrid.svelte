<script lang="ts">
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { DEFAULT_TOP_WIDGETS } from "$lib/types/dashboard-widgets";
  import BgDeltaWidget from "./widgets/BgDeltaWidget.svelte";
  import LastUpdatedWidget from "./widgets/LastUpdatedWidget.svelte";
  import ConnectionStatusWidget from "./widgets/ConnectionStatusWidget.svelte";
  import MealsWidget from "./widgets/MealsWidget.svelte";
  import TrackersWidget from "./widgets/TrackersWidget.svelte";
  import TirChartWidget from "./widgets/TirChartWidget.svelte";
  import DailySummaryWidget from "./widgets/DailySummaryWidget.svelte";
  import ClockWidget from "./widgets/ClockWidget.svelte";
  import TddWidget from "./widgets/TddWidget.svelte";

  interface Props {
    /** Ordered list of widget IDs to display */
    widgets?: WidgetId[];
    /** Maximum number of widgets to show (default 3) */
    maxWidgets?: number;
  }

  let { widgets = DEFAULT_TOP_WIDGETS, maxWidgets = 3 }: Props = $props();

  // Limit to max widgets
  const displayWidgets = $derived(widgets.slice(0, maxWidgets));

  // Widget component map using enum values
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const widgetComponents: Partial<Record<WidgetId, any>> = {
    [WidgetId.BgDelta]: BgDeltaWidget,
    [WidgetId.LastUpdated]: LastUpdatedWidget,
    [WidgetId.ConnectionStatus]: ConnectionStatusWidget,
    [WidgetId.Meals]: MealsWidget,
    [WidgetId.Trackers]: TrackersWidget,
    [WidgetId.TirChart]: TirChartWidget,
    [WidgetId.DailySummary]: DailySummaryWidget,
    [WidgetId.Clock]: ClockWidget,
    [WidgetId.Tdd]: TddWidget,
  };
</script>

<div class="@container grid grid-cols-1 @md:grid-cols-3 gap-2 @md:gap-4">
  {#each displayWidgets as widgetId (widgetId)}
    {@const WidgetComponent = widgetComponents[widgetId] as typeof BgDeltaWidget | undefined}
    {#if WidgetComponent}
      <WidgetComponent />
    {/if}
  {/each}
</div>
