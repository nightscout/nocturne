<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import type { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import {
    DEFAULT_TOP_WIDGETS,
    TOP_WIDGET_IDS,
    knownTopWidgets,
    type TopWidgetId,
  } from "$lib/components/dashboard/widget-registry";
  import { WIDGET_ICONS } from "$lib/types/dashboard-widgets";
  import { GripVertical, LayoutGrid, Plus, X } from "lucide-svelte";
  interface Props {
    /** Currently selected widget IDs (ordered) */
    value: WidgetId[];
    /** Callback when widgets change */
    onchange?: (widgets: TopWidgetId[]) => void;
    /** Maximum number of widgets allowed */
    maxWidgets?: number;
  }

  let {
    value = [...DEFAULT_TOP_WIDGETS],
    onchange,
    maxWidgets = 3,
  }: Props = $props();

  // Local state for drag operations
  let draggedIndex: number | null = $state(null);
  let dragOverIndex: number | null = $state(null);

  const selectedWidgets = $derived(knownTopWidgets(value));
  const availableWidgets = $derived(
    TOP_WIDGET_IDS.filter((id) => !selectedWidgets.includes(id))
  );
  const canAddMore = $derived(selectedWidgets.length < maxWidgets);

  // Get widget display name
  function getWidgetName(id: TopWidgetId): string {
    // Convert camelCase to Title Case
    return id
      .replace(/([A-Z])/g, ' $1')
      .trim()
      .split(' ')
      .map(word => word.charAt(0).toUpperCase() + word.slice(1))
      .join(' ');
  }

  // Drag handlers for reordering
  function handleDragStart(event: DragEvent, index: number) {
    draggedIndex = index;
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = "move";
      event.dataTransfer.setData("text/plain", String(index));
    }
  }

  function handleDragOver(event: DragEvent, index: number) {
    event.preventDefault();
    if (draggedIndex !== null && draggedIndex !== index) {
      dragOverIndex = index;
    }
  }

  function handleDragLeave() {
    dragOverIndex = null;
  }

  function handleDrop(event: DragEvent, targetIndex: number) {
    event.preventDefault();

    if (draggedIndex !== null && draggedIndex !== targetIndex) {
      const newValue = [...selectedWidgets];
      const [removed] = newValue.splice(draggedIndex, 1);
      newValue.splice(targetIndex, 0, removed);
      onchange?.(newValue);
    }

    draggedIndex = null;
    dragOverIndex = null;
  }

  function handleDragEnd() {
    draggedIndex = null;
    dragOverIndex = null;
  }

  // Add widget
  function addWidget(id: TopWidgetId) {
    if (canAddMore) {
      onchange?.([...selectedWidgets, id]);
    }
  }

  // Remove widget
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
      Dashboard Widgets
    </CardTitle>
    <CardDescription>
      Customize the {maxWidgets} widgets shown above the glucose chart. Drag to reorder.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4 @container">
    <!-- Selected widgets (draggable) -->
    <div class="space-y-2">
      <span class="text-sm font-medium">Active Widgets</span>
      <div class="space-y-2">
        {#each selectedWidgets as widgetId, index (widgetId)}
          {@const Icon = WIDGET_ICONS[widgetId]}
          <div
            class="flex items-center gap-2 p-3 rounded-lg border bg-card transition-all
              {dragOverIndex === index
              ? 'border-primary bg-accent'
              : 'border-border'}
              {draggedIndex === index ? 'opacity-50' : ''}"
            draggable="true"
            ondragstart={(e) => handleDragStart(e, index)}
            ondragover={(e) => handleDragOver(e, index)}
            ondragleave={handleDragLeave}
            ondrop={(e) => handleDrop(e, index)}
            ondragend={handleDragEnd}
            role="listitem"
          >
            <GripVertical class="h-4 w-4 text-muted-foreground cursor-grab" />
            <Badge variant="outline" class="w-6 h-6 p-0 justify-center">
              {index + 1}
            </Badge>
            <Icon class="h-4 w-4 text-muted-foreground" />
            <div class="flex-1 min-w-0">
              <div class="font-medium text-sm">{getWidgetName(widgetId)}</div>
            </div>
            <Button
              variant="ghost"
              size="sm"
              class="h-8 w-8 p-0 text-muted-foreground hover:text-destructive"
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
          Available Widgets {#if !canAddMore}(max {maxWidgets} reached){/if}
        </span>
        <div class="grid grid-cols-1 @sm:grid-cols-2 gap-2">
          {#each availableWidgets as widgetId (widgetId)}
            {@const Icon = WIDGET_ICONS[widgetId]}
            <button
              type="button"
              class="flex items-center gap-2 p-2 rounded-lg border border-dashed text-left transition-colors
                {canAddMore
                ? 'hover:border-primary hover:bg-accent cursor-pointer'
                : 'opacity-50 cursor-not-allowed'}"
              onclick={() => addWidget(widgetId)}
              disabled={!canAddMore}
            >
              <Plus class="h-4 w-4 text-muted-foreground" />
              <Icon class="h-4 w-4 text-muted-foreground" />
              <div class="flex-1 min-w-0">
                <div class="font-medium text-sm">{getWidgetName(widgetId)}</div>
              </div>
            </button>
          {/each}
        </div>
      </div>
    {/if}

    <p class="text-xs text-muted-foreground">
      Changes are saved automatically when you leave this page.
    </p>
  </CardContent>
</Card>
