<!--
  One panel of a stack sharing an x axis of the cursor's slots: by default the 288 five-minute slots
  of the local day, or any other run of slots whose labels and ticks the caller gives. The x domain
  is slot units, so a mark at slot i spans [i, i + 1). The stack is one tab stop: left and right
  (with Home and End) move along the axis, up and down move between panels, a click or Enter picks
  the slot for the list it opens and Escape closes it. The readout opens below the plot, beside the
  crosshair, so it covers neither the plot nor the marks under the pointer.
-->
<script lang="ts">
  import { untrack, type Snippet } from "svelte";
  import { Axis, Chart, Rule, Svg } from "layerchart";
  import ChartKey, { type ChartKeyItem } from "#lib/components/charts/print/ChartKey.svelte";
  import { hourLabel, time } from "#lib/utils/formatting.js";
  import { atMinute, SLOT_MINUTES, slotKeyAction } from "./daily-profile-view";
  import type { TimeOfDayCursor } from "./time-of-day-cursor.svelte";

  interface Props {
    id: string;
    title: string;
    unit: string;
    cursor: TimeOfDayCursor;
    yDomain: [number, number];
    yFormat?: (value: number) => string;
    /** Shown in place of the plot when the panel has nothing to draw. */
    empty?: string | null;
    /** Says which of the panel's series the caller's access hides, when the rest still draws. */
    hiddenNote?: string | null;
    showTimeAxis?: boolean;
    heightClass?: string;
    legend?: ChartKeyItem[];
    marks: Snippet;
    tooltip: Snippet<[number]>;
    /** The slot's headline value and n, read out with its time as the slider's value. */
    describe: (slot: number) => string;
    /** Where the x axis is ticked, in slots; every three hours of the day by default. */
    ticks?: number[];
    /** A tick's label; the hour of the day by default. */
    tickLabel?: (slot: number) => string;
    /** A slot's span, as the tooltip and the slider name it; its time of day by default. */
    slotLabel?: (slot: number) => string;
    /** Heads the readout with the slot's span; off for a panel whose readout names its own hour. */
    slotHeading?: boolean;
    /** What Enter and Escape do on this page, for the key help; null where Enter does nothing. */
    pickHelp?: string | null;
  }

  let {
    id,
    title,
    unit,
    cursor,
    yDomain,
    yFormat,
    empty = null,
    hiddenNote = null,
    showTimeAxis = false,
    heightClass = "h-36",
    legend = [],
    marks,
    tooltip,
    describe,
    ticks = [0, 3, 6, 9, 12, 15, 18, 21, 24].map((h) => h * 12),
    tickLabel = (slot: number) => hourLabel(atMinute(slot * SLOT_MINUTES)),
    slotLabel = (slot: number) =>
      `${time(atMinute(slot * SLOT_MINUTES))}–${time(atMinute((slot + 1) * SLOT_MINUTES))}`,
    slotHeading = true,
    pickHelp = null,
  }: Props = $props();

  const unitId = $derived(`time-of-day-${id}-unit`);
  const keysId = $derived(`time-of-day-${id}-keys`);

  const slotCount = $derived(cursor.slotCount);

  // Left and right are written into the tooltip's position classes too.
  const padding = $derived({ top: 8, right: 12, bottom: showTimeAxis ? 24 : 6, left: 44 });
  // As many decimals as the axis span needs, so a small range does not repeat a label.
  const tickDigits = $derived.by(() => {
    const span = yDomain[1] - yDomain[0];
    return span < 1.5 ? 2 : span < 6 ? 1 : 0;
  });
  const formatTick = $derived(yFormat ?? ((v: number) => v.toFixed(tickDigits)));

  let width = $state(0);
  // Every other (or every third) time label on a narrow plot, so the labels do not run together.
  const MIN_TICK_SPACING = 40;
  const shownTicks = $derived.by(() => {
    const plot = width - padding.left - padding.right;
    if (plot <= 0) return ticks;
    const step = Math.max(1, Math.ceil(((ticks.length - 1) * MIN_TICK_SPACING) / plot));
    return ticks.filter((_, i) => i % step === 0);
  });

  function slotAt(event: PointerEvent | MouseEvent, element: HTMLElement): number | null {
    const box = element.getBoundingClientRect();
    const width = box.width - padding.left - padding.right;
    if (width <= 0) return null;
    const fraction = (event.clientX - box.left - padding.left) / width;
    if (fraction < 0 || fraction >= 1) return null;
    return Math.floor(fraction * slotCount);
  }

  function onpointermove(event: PointerEvent & { currentTarget: HTMLElement }) {
    const slot = slotAt(event, event.currentTarget);
    if (slot == null) cursor.leave(id);
    else cursor.hover(id, slot);
  }

  function onclick(event: MouseEvent & { currentTarget: HTMLElement }) {
    const slot = slotAt(event, event.currentTarget);
    if (slot != null) cursor.select(slot);
  }

  function onkeydown(event: KeyboardEvent) {
    const action = slotKeyAction(event, slotCount - 1);
    if (action == null) return;
    if (action.kind === "close") {
      if (cursor.selected == null) return;
      cursor.selected = null;
    } else if (action.kind === "select") cursor.select(current, true);
    else if (action.kind === "step") cursor.hover(id, current + action.by);
    else if (action.kind === "to") cursor.hover(id, action.slot);
    else cursor.focusNeighbour(id, action.by);
    event.preventDefault();
  }

  function onfocus() {
    cursor.tabStop = id;
    cursor.hover(id, current);
  }

  let element = $state<HTMLElement | null>(null);
  $effect(() => {
    const target = element;
    if (target) return untrack(() => cursor.register(id, target));
  });

  const current = $derived(cursor.slot ?? cursor.selected ?? 0);
  const tooltipSlot = $derived(cursor.panel === id ? cursor.slot : null);
</script>

<div class="space-y-1" data-testid="loop-analysis-panel-{id}">
  <div class="flex flex-wrap items-baseline justify-between gap-x-2 px-1">
    <h3 class="text-sm font-medium">{title}</h3>
    <span id={unitId} class="text-xs text-muted-foreground">{unit}</span>
  </div>

  {#if empty}
    <div
      class="flex {heightClass} items-center justify-center rounded-md border border-dashed text-center text-sm text-muted-foreground"
    >
      <p class="px-4">{empty}</p>
    </div>
  {:else}
    <div
      class="relative {heightClass} w-full cursor-crosshair touch-pan-y rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
      bind:this={element}
      bind:clientWidth={width}
      role="slider"
      tabindex={cursor.tabStop === id ? 0 : -1}
      aria-label={title}
      aria-describedby="{unitId} {keysId}"
      aria-valuemin={0}
      aria-valuemax={slotCount - 1}
      aria-valuenow={current}
      aria-valuetext={`${slotLabel(current)}: ${describe(current)}`}
      {onpointermove}
      onpointerleave={() => cursor.leave(id)}
      {onfocus}
      onblur={() => cursor.leave(id)}
      {onclick}
      {onkeydown}
    >
      <span id={keysId} class="sr-only">
        Use the left and right arrows to move along the axis. Use Home and End to go to its ends. Use
        the up arrow to go to the previous chart and the down arrow to go to the next chart.
        {pickHelp ?? ""}
      </span>
      <Chart data={[]} xDomain={[0, slotCount]} {yDomain} {padding}>
        <Svg>
          <Axis
            placement="left"
            ticks={3}
            grid={{ class: "stroke-border/60" }}
            format={formatTick}
            tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
          />
          {@render marks()}
          {#if cursor.selected != null}
            <Rule x={cursor.selected + 0.5} class="stroke-primary" stroke-dasharray="3 2" />
          {/if}
          {#if cursor.slot != null}
            <Rule x={cursor.slot + 0.5} class="stroke-foreground/60" />
          {/if}
          <Axis
            placement="bottom"
            rule={{ class: "stroke-border" }}
            ticks={shownTicks}
            format={showTimeAxis ? tickLabel : () => ""}
            tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
          />
        </Svg>
      </Chart>

      {#if tooltipSlot != null}
        {@const fraction = (tooltipSlot + 0.5) / slotCount}
        <!-- Below the plot, beside the crosshair on whichever side has room; the offsets are the plot padding. -->
        <div
          class="pointer-events-none absolute top-full z-10 mt-1 min-w-44 rounded-md border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-md {fraction <
          0.6
            ? 'left-[calc(56px_+_(100%_-_56px)_*_var(--tip-at))]'
            : 'right-[calc(24px_+_(100%_-_56px)_*_(1_-_var(--tip-at)))]'}"
          style:--tip-at={fraction}
          role="tooltip"
        >
          {#if slotHeading}
            <p class="mb-1 font-medium">{slotLabel(tooltipSlot)}</p>
          {/if}
          {@render tooltip(tooltipSlot)}
        </div>
      {/if}
    </div>
    {#if legend.length > 0}
      <ChartKey class="justify-start px-1" items={legend} />
    {/if}
    {#if hiddenNote}
      <p class="px-1 text-xs text-muted-foreground">{hiddenNote}</p>
    {/if}
  {/if}
</div>
