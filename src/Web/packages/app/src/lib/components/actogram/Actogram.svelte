<script lang="ts" generics="T extends ActogramPoint">
  import { formatShortDate } from "$lib/utils/formatting";
  import type { Snippet } from 'svelte';
  import type {
    ActogramPoint,
    ActogramRowContext,
    GlucosePoint,
    GlucoseThresholds,
  } from './actogram';
  import {
    sliceIntoRows,
    sliceBgIntoRows,
    resolveTargetRange,
    HOURS_PER_ROW,
  } from './actogram';
  import ActogramRow from './ActogramRow.svelte';
  import { untrack } from 'svelte';
  import { fly } from 'svelte/transition';
  import { flip } from 'svelte/animate';
  import { cubicOut } from 'svelte/easing';
  import { ChevronUp, ChevronDown } from 'lucide-svelte';
  import { Button } from '$lib/components/ui/button';
  import { bgRange } from '$lib/utils/formatting';
  import { PrintMode } from '$lib/components/charts/print/print-mode.svelte';
  import ChartKey, { type ChartKeyItem } from '$lib/components/charts/print/ChartKey.svelte';

  interface Props {
    data: T[];
    bgData?: GlucosePoint[];
    days: Date[];
    thresholds?: GlucoseThresholds;
    rowHeight?: number;
    visibleCount?: number;
    /** Leading rows a print carries: the report's range, without scroll-back padding. */
    printCount?: number;
    initialOffset?: number;
    onVisibleRangeChange?: (from: Date, to: Date) => void;
    row: Snippet<[ActogramRowContext<T>]>;
    tooltipValue?: Snippet<[{ point: T; day: Date }]>;
    rowLabel?: Snippet<[{ day: Date }]>;
    /** Key for the marks the `row` snippet draws; glucose overlay entries are added. */
    legend?: ChartKeyItem[];
  }

  let {
    data,
    bgData,
    days,
    thresholds,
    rowHeight = 48,
    visibleCount,
    printCount,
    initialOffset,
    onVisibleRangeChange,
    row,
    tooltipValue,
    rowLabel,
    legend = [],
  }: Props = $props();

  const print = new PrintMode();

  const dataRows = $derived(sliceIntoRows(data, days));
  const bgRows = $derived(bgData ? sliceBgIntoRows(bgData, days) : []);

  let offset = $state(untrack(() => initialOffset ?? 0));
  let direction: 'up' | 'down' = $state('down');

  const effectiveVisibleCount = $derived(visibleCount ?? days.length);
  const maxOffset = $derived(Math.max(0, dataRows.length - effectiveVisibleCount));

  // Paper cannot page through rows, so a print carries the whole range.
  const shownRange = $derived<[number, number]>(
    print.active
      ? [0, printCount ?? dataRows.length]
      : [offset, offset + effectiveVisibleCount]
  );
  const visibleDataRows = $derived(dataRows.slice(...shownRange));
  const visibleBgRows = $derived(bgRows.slice(...shownRange));

  // X-axis hour labels at 6-hour intervals across 48h double-plot.
  // Labels show hours mod 24, so both 0h and 24h display as "0h" (midnight).
  const hourLabels = [0, 6, 12, 18, 24, 30, 36, 42, 48];

  function navigate(delta: number) {
    direction = delta > 0 ? 'down' : 'up';
    const newOffset = Math.max(0, Math.min(maxOffset, offset + delta));
    offset = newOffset;
    if (onVisibleRangeChange) {
      const visible = dataRows.slice(newOffset, newOffset + effectiveVisibleCount);
      if (visible.length > 0) {
        onVisibleRangeChange(visible[0].day, visible[visible.length - 1].day);
      }
    }
  }

  function handleKeydown(e: KeyboardEvent) {
    if (visibleCount === undefined) return;

    let delta = 0;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      const sign = e.key === 'ArrowDown' ? 1 : -1;
      if (e.altKey) delta = 30 * sign;
      else if (e.shiftKey) delta = 7 * sign;
      else delta = 1 * sign;
    }

    if (delta !== 0) {
      e.preventDefault();
      navigate(delta);
    }
  }

  function formatDate(date: Date): string {
    return formatShortDate(date);
  }
</script>

<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
<div
  class="flex flex-col w-full"
  tabindex={visibleCount !== undefined ? 0 : undefined}
  onkeydown={handleKeydown}
  role={visibleCount !== undefined ? 'toolbar' : undefined}
  aria-label={visibleCount !== undefined ? 'Actogram with keyboard navigation' : undefined}
>
  <!-- X-axis labels (top) -->
  <div class="flex">
    <div class="w-20 shrink-0 flex items-center justify-center">
      {#if visibleCount !== undefined}
        <Button
          variant="ghost-muted"
          size="icon-2xs"
          class="print:hidden"
          disabled={offset === 0}
          onclick={() => navigate(-7)}
          aria-label="Previous week"
        >
          <ChevronUp class="size-4" />
        </Button>
      {/if}
    </div>
    <div class="flex-1 relative h-6">
      {#each hourLabels as hour (hour)}
        {@const pct = (hour / HOURS_PER_ROW) * 100}
        <span
          class="absolute left-(--hour-left) text-xs text-muted-foreground -translate-x-1/2"
          style:--hour-left="{pct}%"
        >
          {hour % 24 === 0 && hour < 48 ? '0' : hour % 24}h
        </span>
      {/each}
    </div>
  </div>

  <!-- Rows -->
  {#each visibleDataRows as dataRow, i (`${dataRow.day.getTime()}-${i}`)}
    <div
      class="flex h-(--row-h) items-center break-inside-avoid"
      style:--row-h="{rowHeight}px"
      animate:flip={{ duration: 300, easing: cubicOut }}
      in:fly={{ y: direction === 'down' ? rowHeight : -rowHeight, duration: 300, easing: cubicOut }}
      out:fly={{ y: direction === 'down' ? -rowHeight : rowHeight, duration: 300, easing: cubicOut }}
    >
      <!-- Date label -->
      <div class="w-20 shrink-0">
        {#if rowLabel}
          {@render rowLabel({ day: dataRow.day })}
        {:else}
          <span class="block text-xs text-muted-foreground text-right pr-2">
            {formatDate(dataRow.day)}
          </span>
        {/if}
      </div>
      <!-- Chart row -->
      <div class="flex-1 h-full border-b border-border/30">
        <ActogramRow
          day={dataRow.day}
          data={dataRow.data}
          bgData={visibleBgRows[i]?.bgData ?? []}
          {thresholds}
          height={rowHeight}
          {row}
          {tooltipValue}
        />
      </div>
    </div>
  {/each}

  {#if legend.length > 0}
    <ChartKey items={legend} class="mt-3 justify-start pl-20" />
  {/if}
  {#if bgData?.length && thresholds}
    {@const target = resolveTargetRange(thresholds)}
    <!-- On screen the glucose dots carry their range in colour and a tooltip names them. -->
    <ChartKey
      items={[
        { texture: 'glucose-trace', label: 'Glucose', shape: 'line' },
        { texture: 'target-range-limit', label: `Glucose target range ${bgRange(target.low, target.high)}`, color: 'var(--muted-foreground)', shape: 'line' },
      ]}
      class="mt-1 hidden justify-start pl-20 [.chart-patterns-on_&]:flex"
    />
  {/if}

  {#if visibleCount !== undefined}
    <div class="flex print:hidden">
      <div class="w-20 shrink-0 flex items-center justify-center">
        <Button
          variant="ghost-muted"
          size="icon-2xs"
          disabled={offset >= maxOffset}
          onclick={() => navigate(7)}
          aria-label="Next week"
        >
          <ChevronDown class="size-4" />
        </Button>
      </div>
      <div class="flex-1"></div>
    </div>
  {/if}
</div>
