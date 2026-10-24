<script lang="ts">
  import { tick } from "svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import {
    RailItemId,
    type DashboardRailPreference,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    RAIL_ITEM_IDS,
    isRailItemHidden,
    railOrder,
  } from "$lib/components/status-pills/rail";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import GripVertical from "@lucide/svelte/icons/grip-vertical";
  import Pill from "@lucide/svelte/icons/pill";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import { DragReorder, moveItem } from "./drag-reorder.svelte";

  interface Props {
    /** The user's arrangement of the rail; an empty object is the default. */
    value: DashboardRailPreference;
    /** The tenant's tracker pill switch. While it is off, no user sees trackers. */
    trackerPillsEnabled: boolean;
    onchange?: (preference: DashboardRailPreference) => void;
  }

  let { value, trackerPillsEnabled, onchange }: Props = $props();

  const LABELS: Record<RailItemId, string> = {
    [RailItemId.Cob]: "Carbs on board (COB)",
    [RailItemId.Basal]: "Basal rate",
    [RailItemId.Iob]: "Insulin on board (IOB)",
    [RailItemId.Loop]: "Loop status",
    [RailItemId.Reservoir]: "Reservoir",
    [RailItemId.Trackers]: "Trackers",
  };

  const order = $derived(railOrder(value));
  const hidden = $derived(RAIL_ITEM_IDS.filter((id) => isRailItemHidden(value, id)));
  const isDefault = $derived(
    hidden.length === 0 && order.every((id, i) => id === RAIL_ITEM_IDS[i])
  );
  const nothingShown = $derived(
    order.every(
      (id) =>
        hidden.includes(id) || (id === RailItemId.Trackers && !trackerPillsEnabled)
    )
  );

  let listEl = $state<HTMLElement>();

  function write(nextOrder: RailItemId[], nextHidden: RailItemId[]) {
    onchange?.({ order: nextOrder, hidden: nextHidden });
  }

  const drag = new DragReorder<RailItemId>(
    () => order,
    (items) => write(items, hidden)
  );

  /** Put focus back on a control of a row once the list has re-rendered. */
  async function refocus(id: RailItemId, control: string, fallback?: string) {
    await tick();
    const row = listEl?.querySelector(`[data-rail-item="${id}"]`);
    const target = row?.querySelector<HTMLElement>(`[data-control="${control}"]`);
    const usable = target && !target.hasAttribute("disabled") ? target : null;
    (usable ?? row?.querySelector<HTMLElement>(`[data-control="${fallback}"]`))?.focus();
  }

  function move(id: RailItemId, index: number, to: number) {
    write(moveItem(order, index, to), hidden);
    const control = to < index ? "up" : "down";
    void refocus(id, control, control === "up" ? "down" : "up");
  }

  function setShown(id: RailItemId, show: boolean) {
    write(order, show ? hidden.filter((item) => item !== id) : [...hidden, id]);
    void refocus(id, "show");
  }
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Pill class="h-5 w-5" />
      Status Pills
    </CardTitle>
    <CardDescription>
      Select the pills that show next to the glucose value. Drag a pill to change its position.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    <div bind:this={listEl} class="space-y-2" role="list" aria-label="Status pills">
      {#each order as id, index (id)}
        {@const unavailable = id === RailItemId.Trackers && !trackerPillsEnabled}
        {@const shown = !hidden.includes(id)}
        <div
          class="flex items-center gap-2 rounded-lg border p-3 transition-all
          {drag.dragOverIndex === index ? 'border-primary bg-accent' : 'border-border bg-card'}
          {drag.draggedIndex === index ? 'opacity-50' : ''}"
          draggable="true"
          ondragstart={(e) => drag.start(e, index)}
          ondragover={(e) => drag.over(e, index)}
          ondragleave={() => drag.leave()}
          ondrop={(e) => drag.drop(e, index)}
          ondragend={() => drag.end()}
          role="listitem"
          data-rail-item={id}
          data-shown={shown && !unavailable}
        >
          <GripVertical class="h-4 w-4 cursor-grab text-muted-foreground" />
          <Badge variant="outline" class="h-6 min-w-6 tabular-nums">
            {index + 1}
          </Badge>
          <div class="flex-1 min-w-0">
            <div
              class="text-sm font-medium {shown && !unavailable ? '' : 'text-muted-foreground'}"
            >
              {LABELS[id]}
            </div>
            {#if unavailable}
              <p class="text-xs text-muted-foreground">
                The owner turned off tracker pills.
              </p>
            {/if}
          </div>
          <Button
            variant="ghost-muted"
            size="icon-sm"
            aria-label={`Move ${LABELS[id]} up`}
            data-control="up"
            disabled={index === 0}
            onclick={() => move(id, index, index - 1)}
          >
            <ArrowUp class="h-4 w-4" />
          </Button>
          <Button
            variant="ghost-muted"
            size="icon-sm"
            aria-label={`Move ${LABELS[id]} down`}
            data-control="down"
            disabled={index === order.length - 1}
            onclick={() => move(id, index, index + 1)}
          >
            <ArrowDown class="h-4 w-4" />
          </Button>
          {#if !unavailable}
            <Switch
              checked={shown}
              aria-label={`Show ${LABELS[id]}`}
              data-control="show"
              onCheckedChange={(checked: boolean) => setShown(id, checked)}
            />
          {/if}
        </div>
      {/each}
    </div>

    {#if nothingShown}
      <p class="rounded-lg border border-dashed py-6 text-center text-sm text-muted-foreground">
        No pills show on the dashboard.
      </p>
    {/if}

    <div class="flex items-center justify-between gap-2">
      <p class="text-xs text-muted-foreground">
        Trackers show only when a tracker is active.
      </p>
      <Button
        variant="outline"
        size="sm"
        disabled={isDefault}
        onclick={() => onchange?.({})}
      >
        <RotateCcw class="h-4 w-4" />
        Reset to default
      </Button>
    </div>
  </CardContent>
</Card>
