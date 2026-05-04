<script lang="ts">
  import type { Snippet } from 'svelte';
  import type {
    ActogramPoint,
    ActogramRowContext,
    GlucosePoint,
    GlucoseThresholds,
  } from './actogram';
  import { sliceIntoRows, sliceBgIntoRows, HOURS_PER_ROW } from './actogram';
  import ActogramRow from './ActogramRow.svelte';
  import { untrack } from 'svelte';
  import { fly } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import { ChevronUp, ChevronDown } from 'lucide-svelte';

  interface Props {
    data: ActogramPoint[];
    bgData?: GlucosePoint[];
    days: Date[];
    thresholds?: GlucoseThresholds;
    rowHeight?: number;
    visibleCount?: number;
    initialOffset?: number;
    onVisibleRangeChange?: (from: Date, to: Date) => void;
    row: Snippet<[ActogramRowContext]>;
    tooltipValue?: Snippet<[{ point: ActogramPoint; day: Date }]>;
  }

  let {
    data,
    bgData,
    days,
    thresholds,
    rowHeight = 48,
    visibleCount,
    initialOffset,
    onVisibleRangeChange,
    row,
    tooltipValue,
  }: Props = $props();

  const dataRows = $derived(sliceIntoRows(data, days));
  const bgRows = $derived(bgData ? sliceBgIntoRows(bgData, days) : []);

  let offset = $state(untrack(() => initialOffset ?? 0));
  let direction: 'up' | 'down' = $state('down');

  const effectiveVisibleCount = $derived(visibleCount ?? days.length);
  const maxOffset = $derived(Math.max(0, dataRows.length - effectiveVisibleCount));

  const visibleDataRows = $derived(dataRows.slice(offset, offset + effectiveVisibleCount));
  const visibleBgRows = $derived(bgRows.slice(offset, offset + effectiveVisibleCount));

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
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
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
    <div class="w-16 shrink-0 flex items-center justify-center">
      {#if visibleCount !== undefined}
        <button
          class="text-muted-foreground hover:text-foreground disabled:opacity-30 transition-colors p-0.5"
          disabled={offset === 0}
          onclick={() => navigate(-7)}
          aria-label="Previous week"
        >
          <ChevronUp class="size-4" />
        </button>
      {/if}
    </div>
    <div class="flex-1 relative h-6">
      {#each hourLabels as hour (hour)}
        {@const pct = (hour / HOURS_PER_ROW) * 100}
        <span
          class="absolute text-xs text-muted-foreground -translate-x-1/2"
          style:left="{pct}%"
        >
          {hour % 24 === 0 && hour < 48 ? '0' : hour % 24}h
        </span>
      {/each}
    </div>
  </div>

  <!-- Rows -->
  {#each visibleDataRows as dataRow, i (dataRow.day.getTime())}
    <div
      class="flex items-center"
      style:height="{rowHeight}px"
      in:fly={{ y: direction === 'down' ? rowHeight : -rowHeight, duration: 300, easing: cubicOut }}
      out:fly={{ y: direction === 'down' ? -rowHeight : rowHeight, duration: 300, easing: cubicOut }}
    >
      <!-- Date label -->
      <div class="w-16 shrink-0 text-xs text-muted-foreground text-right pr-2">
        {formatDate(dataRow.day)}
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

  {#if visibleCount !== undefined}
    <div class="flex">
      <div class="w-16 shrink-0 flex items-center justify-center">
        <button
          class="text-muted-foreground hover:text-foreground disabled:opacity-30 transition-colors p-0.5"
          disabled={offset >= maxOffset}
          onclick={() => navigate(7)}
          aria-label="Next week"
        >
          <ChevronDown class="size-4" />
        </button>
      </div>
      <div class="flex-1"></div>
    </div>
  {/if}
</div>
