<script lang="ts">
  /** A context lane of the therapy timeline: stretches of time as labeled bars, and device changes as points. */
  import type { Snippet } from "svelte";
  import { formatDateTimeCompact } from "#lib/utils/formatting.js";
  import { formatElapsedDuration } from "#lib/utils/duration.js";
  import { fractionOf } from "./timeline-format.js";

  interface Span {
    id: string;
    label: string;
    startMills: number;
    endMills: number;
    color: string;
  }

  interface Marker {
    id: string;
    mills: number;
    title: string;
  }

  interface Props {
    label: string;
    xDomain: [number, number];
    spans?: Span[];
    markers?: Marker[];
    marker?: Snippet<[Marker]>;
  }

  let { label, xDomain, spans = [], markers = [], marker }: Props = $props();

  function describe(span: Span): string {
    const start = new Date(span.startMills);
    const end = new Date(span.endMills);
    return `${span.label ? `${span.label}, ` : ""}${formatDateTimeCompact(start)} – ${formatDateTimeCompact(end)} (${formatElapsedDuration(start, end)})`;
  }
</script>

<div class="grid grid-cols-[var(--tl-label)_minmax(0,1fr)_var(--tl-axis)] border-t border-border">
  <div class="flex items-center pr-2 py-1">
    <span class="text-xs font-medium leading-tight">{label}</span>
  </div>
  <div class="relative h-7">
    {#each spans as span (span.id)}
      {@const left = fractionOf(span.startMills, xDomain[0], xDomain[1])}
      {@const right = fractionOf(span.endMills, xDomain[0], xDomain[1])}
      <div
        class="absolute inset-y-1 overflow-hidden rounded-sm border border-(--span-color)/60 bg-(--span-color)/25 px-1 text-2xs leading-4.5 whitespace-nowrap text-foreground left-(--span-left) w-(--span-width) min-w-0.5"
        style:--span-color={span.color}
        style:--span-left="{left * 100}%"
        style:--span-width="{(right - left) * 100}%"
        title={describe(span)}
      >
        {span.label}
      </div>
    {/each}
    {#each markers as point (point.id)}
      <div
        class="absolute top-1/2 flex size-5 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full border border-border bg-card left-(--marker-left)"
        style:--marker-left="{fractionOf(point.mills, xDomain[0], xDomain[1]) * 100}%"
        title="{point.title}, {formatDateTimeCompact(new Date(point.mills))}"
      >
        {@render marker?.(point)}
      </div>
    {/each}
  </div>
  <div></div>
</div>
