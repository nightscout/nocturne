<script lang="ts">
  import type { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { knownTopWidgets, loadTopWidget } from "./widget-registry";
  import WidgetCard from "./widgets/WidgetCard.svelte";
  import { Card } from "$lib/components/ui/card";

  interface Props {
    /** Ordered list of widget IDs to display */
    widgets: WidgetId[];
    /** Maximum number of widgets to show (default 3) */
    maxWidgets?: number;
  }

  let { widgets, maxWidgets = 3 }: Props = $props();

  const displayWidgets = $derived(knownTopWidgets(widgets).slice(0, maxWidgets));
</script>

<Card size="flush" class="@container">
  <div class="grid grid-cols-1 divide-y divide-border @md:grid-cols-3 @md:divide-x @md:divide-y-0">
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
</Card>
