<script lang="ts">
  /**
   * The whole range at a glance: one block per profile version, a mark at each
   * change, and the glucose trace. The window drawn on it is the stretch the
   * stage shows; a press or drag on the track moves it.
   */
  import { Chart, Svg, Area, Spline } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { curveLinear } from "d3";
  import type { GlucoseTracePoint, ProfileChange, ProfileVersion } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { formatDateTimeCompact, formatShortDate } from "#lib/utils/formatting.js";
  import { fractionOf, isSwitch, versionLabel } from "./timeline-format.js";

  interface Props {
    range: [number, number];
    versions: ProfileVersion[];
    changes: ProfileChange[];
    glucose: GlucoseTracePoint[] | null;
    glucoseYMax: number;
    /** The stage's window, or null when the stage does not draw one. */
    window: [number, number] | null;
    selectedChange: number | null;
    onWindowMove?: (start: number) => void;
    onSelectChange?: (mills: number) => void;
  }

  let {
    range,
    versions,
    changes,
    glucose,
    glucoseYMax,
    window: stageWindow,
    selectedChange,
    onWindowMove,
    onSelectChange,
  }: Props = $props();

  const TONES = ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)", "var(--chart-4)", "var(--chart-5)"];

  /** One tone per profile name, so a profile that comes back reads as the same one. */
  const toneOf = $derived.by(() => {
    const names = [...new Set(versions.map((v) => v.profileName ?? ""))];
    return (name: string | undefined) => TONES[names.indexOf(name ?? "") % TONES.length];
  });

  const trace = $derived(
    (glucose ?? []).map((p) => ({ t: p.mills ?? 0, v: p.average ?? 0, low: p.min ?? 0, high: p.max ?? 0 }))
  );

  let track = $state<HTMLDivElement | null>(null);
  let dragStart = $state<number | null>(null);

  const windowLength = $derived(stageWindow ? stageWindow[1] - stageWindow[0] : 0);
  const shownStart = $derived(dragStart ?? stageWindow?.[0] ?? null);

  function startAt(clientX: number): number {
    const box = track!.getBoundingClientRect();
    const at = range[0] + ((clientX - box.left) / box.width) * (range[1] - range[0]);
    return Math.round(Math.min(range[1] - windowLength, Math.max(range[0], at - windowLength / 2)));
  }

  function onPointerDown(event: PointerEvent) {
    if (!stageWindow || !track || event.button !== 0) return;
    track.setPointerCapture(event.pointerId);
    dragStart = startAt(event.clientX);
  }

  function onPointerMove(event: PointerEvent) {
    if (dragStart === null) return;
    dragStart = startAt(event.clientX);
  }

  function onPointerUp() {
    if (dragStart === null) return;
    onWindowMove?.(dragStart);
    dragStart = null;
  }

  const ticks = $derived.by(() => {
    const count = 6;
    return Array.from({ length: count + 1 }, (_, i) => range[0] + ((range[1] - range[0]) * i) / count);
  });
</script>

<div class="space-y-1">
  <!-- Change marks sit above the track so each stays a button of its own. -->
  <div class="relative h-5 {changes.length === 0 ? 'hidden' : ''}">
    {#each changes as change (change.mills)}
      {@const selected = selectedChange === change.mills}
      <span
        class="absolute top-0 left-(--mark-left) -translate-x-1/2"
        style:--mark-left="{fractionOf(change.mills ?? 0, range[0], range[1]) * 100}%"
      >
        <Button
          variant="ghost"
          size="icon-2xs"
          aria-label="{isSwitch(change) ? 'Profile switch' : 'Profile edit'}, {formatDateTimeCompact(new Date(change.mills ?? 0))}"
          aria-pressed={selected}
          onclick={() => onSelectChange?.(change.mills ?? 0)}
        >
          <span
            class="block size-2 rotate-45 border {selected
              ? 'scale-150 border-foreground bg-foreground'
              : isSwitch(change)
                ? 'border-foreground bg-foreground/70'
                : 'border-foreground bg-card'}"
          ></span>
        </Button>
      </span>
    {/each}
  </div>

  <div
    bind:this={track}
    class="relative touch-none select-none {stageWindow ? 'cursor-grab active:cursor-grabbing' : ''}"
    role="presentation"
    onpointerdown={onPointerDown}
    onpointermove={onPointerMove}
    onpointerup={onPointerUp}
    onpointercancel={onPointerUp}
  >
    <div class="relative h-7 overflow-hidden rounded-sm bg-muted/40">
      {#each versions as version (version.startMills)}
        {@const left = fractionOf(version.startMills ?? 0, range[0], range[1])}
        {@const right = fractionOf(version.endMills ?? 0, range[0], range[1])}
        <div
          class="absolute inset-y-0 overflow-hidden border-l border-background bg-(--tone)/30 px-1.5 text-2xs leading-7 font-medium whitespace-nowrap left-(--block-left) w-(--block-width)"
          style:--tone={toneOf(version.profileName)}
          style:--block-left="{left * 100}%"
          style:--block-width="{(right - left) * 100}%"
          title="{versionLabel(version)}, {formatDateTimeCompact(new Date(version.startMills ?? 0))} – {formatDateTimeCompact(new Date(version.endMills ?? 0))}"
        >
          {versionLabel(version)}
        </div>
      {/each}
    </div>

    {#if trace.length > 1}
      <div class="h-12">
        <Chart
          data={[]}
          xScale={scaleLinear()}
          xDomain={range}
          yScale={scaleLinear()}
          yDomain={[0, glucoseYMax]}
          padding={{ top: 4, bottom: 2, left: 0, right: 0 }}
        >
          <Svg>
            <Area
              data={trace}
              x={(d: { t: number }) => d.t}
              y0={(d: { low: number }) => d.low}
              y1={(d: { high: number }) => d.high}
              curve={curveLinear}
              class="fill-glucose-in-range/20"
            />
            <Spline
              data={trace}
              x={(d: { t: number }) => d.t}
              y={(d: { v: number }) => d.v}
              curve={curveLinear}
              class="fill-none stroke-glucose-in-range stroke-1"
            />
          </Svg>
        </Chart>
      </div>
    {/if}

    {#if shownStart !== null}
      <div
        class="pointer-events-none absolute inset-y-0 rounded-sm border border-foreground bg-foreground/8 left-(--window-left) w-(--window-width)"
        style:--window-left="{fractionOf(shownStart, range[0], range[1]) * 100}%"
        style:--window-width="{(windowLength / (range[1] - range[0])) * 100}%"
      ></div>
    {/if}
  </div>

  <div class="relative h-4 text-2xs text-muted-foreground tabular-nums" aria-hidden="true">
    {#each ticks as tick, i (tick)}
      <span
        class="absolute left-(--tick-left) whitespace-nowrap {i % 2 === 1 ? 'max-sm:hidden' : ''} {i === 0 ? '' : i === ticks.length - 1 ? '-translate-x-full' : '-translate-x-1/2'}"
        style:--tick-left="{(i / (ticks.length - 1)) * 100}%"
      >
        {formatShortDate(new Date(tick))}
      </span>
    {/each}
  </div>
</div>
