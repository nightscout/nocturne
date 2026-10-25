<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Item } from "$lib/components/ui/item";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import {
    WidgetPlacement,
    type WidgetId,
  } from "$lib/api/generated/nocturne-api-client";
  import { getWidgetDefinitions } from "$api/generated/metadatas.generated.remote";
  import {
    TOP_WIDGET_IDS,
    isTopWidgetId,
    knownTopWidgets,
    type TopWidgetId,
  } from "$lib/components/dashboard/widget-registry";
  import { WIDGET_ICONS } from "$lib/types/dashboard-widgets";
  import GripVertical from "@lucide/svelte/icons/grip-vertical";
  import LayoutGrid from "@lucide/svelte/icons/layout-grid";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import { DragReorder } from "./drag-reorder.svelte";
  interface Props {
    /** Currently selected widget IDs (ordered) */
    value: WidgetId[];
    /** Callback when widgets change */
    onchange?: (widgets: TopWidgetId[]) => void;
    /** Maximum number of widgets allowed */
    maxWidgets?: number;
  }

  let { value, onchange, maxWidgets = 3 }: Props = $props();

  const definitions = getWidgetDefinitions();

  const unnamed = $derived(definitions.error !== undefined && definitions.current === undefined);
  const loading = $derived(!definitions.current && !unnamed);

  /**
   * The top widgets this build can render, named by the catalogue. Ids stand in
   * for names when the catalogue cannot be fetched, so a selection can still be
   * reordered and saved.
   */
  const offered: { id: TopWidgetId; name: string }[] = $derived(
    unnamed
      ? TOP_WIDGET_IDS.map((id) => ({ id, name: id }))
      : (definitions.current?.definitions ?? []).flatMap((d) => {
          const id = d.id ?? "";
          return d.placement === WidgetPlacement.Top &&
            d.renderable !== false &&
            isTopWidgetId(id)
            ? [{ id, name: d.name || id }]
            : [];
        })
  );
  const widgetNames = $derived(new Map(offered.map((w) => [w.id, w.name])));

  const selectedWidgets = $derived(knownTopWidgets(value));
  const availableWidgets = $derived(
    offered.filter((w) => !selectedWidgets.includes(w.id))
  );
  const canAddMore = $derived(selectedWidgets.length < maxWidgets);

  const drag = new DragReorder<TopWidgetId>(
    () => selectedWidgets,
    (widgets) => onchange?.(widgets)
  );

  function addWidget(id: TopWidgetId) {
    if (canAddMore) {
      onchange?.([...selectedWidgets, id]);
    }
  }

  function removeWidget(index: number) {
    const newValue = [...selectedWidgets];
    newValue.splice(index, 1);
    onchange?.(newValue);
  }
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <LayoutGrid class="h-5 w-5" />
      Dashboard widgets
    </CardTitle>
    <CardDescription>
      Customize the {maxWidgets} widgets shown above the glucose chart. Drag to reorder.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4 @container">
    {#if loading}
      <Skeleton class="h-14 w-full" />
      <Skeleton class="h-14 w-full" />
      <Skeleton class="h-14 w-full" />
    {:else}
      {#if unnamed}
        <p class="text-sm text-muted-foreground">
          Widget names could not be loaded, so widgets are listed by their
          internal id. Choosing and reordering them still works.
        </p>
      {/if}
      <!-- Selected widgets (draggable) -->
      <div class="space-y-2">
        <span class="text-sm font-medium">Active widgets</span>
        <div class="space-y-2">
          {#each selectedWidgets as widgetId, index (widgetId)}
            {@const Icon = WIDGET_ICONS[widgetId]}
            <div
              class="flex items-center gap-2 p-3 rounded-lg border bg-card transition-all
              {drag.dragOverIndex === index
                ? 'border-primary bg-accent'
                : 'border-border'}
              {drag.draggedIndex === index ? 'opacity-50' : ''}"
              draggable="true"
              ondragstart={(e) => drag.start(e, index)}
              ondragover={(e) => drag.over(e, index)}
              ondragleave={() => drag.leave()}
              ondrop={(e) => drag.drop(e, index)}
              ondragend={() => drag.end()}
              role="listitem"
            >
              <GripVertical class="h-4 w-4 text-muted-foreground cursor-grab" />
              <Badge variant="outline" class="min-w-6 h-6 tabular-nums">
                {index + 1}
              </Badge>
              <Icon class="h-4 w-4 text-muted-foreground" />
              <div class="flex-1 min-w-0">
                <div class="font-medium text-sm">
                  {widgetNames.get(widgetId) ?? widgetId}
                </div>
              </div>
              <Button
                variant="ghost-muted"
                size="icon-sm"
                onclick={() => removeWidget(index)}
              >
                <X class="h-4 w-4" />
              </Button>
            </div>
          {/each}

          {#if selectedWidgets.length === 0}
            <div
              class="text-center py-8 text-muted-foreground border border-dashed rounded-lg"
            >
              <p class="text-sm">No widgets selected</p>
              <p class="text-xs">Add widgets from the list below</p>
            </div>
          {/if}
        </div>
      </div>

      <!-- Available widgets to add -->
      {#if availableWidgets.length > 0}
        <div class="space-y-2">
          <span class="text-sm font-medium text-muted-foreground">
            Available widgets {#if !canAddMore}(max {maxWidgets} reached){/if}
          </span>
          <div class="grid grid-cols-1 @sm:grid-cols-2 gap-2">
            {#each availableWidgets as widget (widget.id)}
              {@const Icon = WIDGET_ICONS[widget.id]}
              <Item
                variant="dashed"
                size="sm"
                onclick={() => addWidget(widget.id)}
                disabled={!canAddMore}
              >
                <Plus class="h-4 w-4 text-muted-foreground" />
                <Icon class="h-4 w-4 text-muted-foreground" />
                <div class="flex-1 min-w-0">
                  <div class="font-medium text-sm">{widget.name}</div>
                </div>
              </Item>
            {/each}
          </div>
        </div>
      {/if}

      <p class="text-xs text-muted-foreground">
        Changes are saved automatically when you leave this page.
      </p>
    {/if}
  </CardContent>
</Card>
