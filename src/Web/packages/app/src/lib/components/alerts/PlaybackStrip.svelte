<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Select from "$lib/components/ui/select";
  import { Play, Pause, RotateCcw } from "lucide-svelte";
  import { AlertReplayEventKind, type AlertRuleSeverity } from "$api-clients";
  import { severityVar } from "./severity";
  import { formatDateTime } from "./alertTime";

  interface EventTick {
    tMs: number;
    severity: AlertRuleSeverity | string | undefined;
    ruleId?: string;
    kind?: AlertReplayEventKind;
  }

  interface Props {
    playing: boolean;
    playPct: number;
    maxPct: number;
    currentDate: Date | null;
    speed: number;
    events: EventTick[];
    windowStartMs: number;
    windowEndMs: number;
    onPlayPause: () => void;
    onReset: () => void;
    onSeek: (pct: number) => void;
  }

  let {
    playing,
    playPct,
    maxPct,
    currentDate,
    speed = $bindable(),
    events,
    windowStartMs,
    windowEndMs,
    onPlayPause,
    onReset,
    onSeek,
  }: Props = $props();

  const SPEED_OPTIONS: number[] = [0.25, 0.5, 1, 2];

  const span = $derived(Math.max(1, windowEndMs - windowStartMs));

  // Pre-compute each tick's xPct so the SVG layer just maps over them.
  const tickList = $derived(
    events
      .filter((e) => Number.isFinite(e.tMs))
      .map((e) => ({
        ...e,
        xPct: Math.max(
          0,
          Math.min(100, ((e.tMs - windowStartMs) / span) * 100),
        ),
      })),
  );

  function handleStripPointerDown(e: PointerEvent): void {
    const target = e.currentTarget as SVGSVGElement;
    const rect = target.getBoundingClientRect();
    const pct = Math.max(
      0,
      Math.min(100, ((e.clientX - rect.left) / rect.width) * 100),
    );
    onSeek(pct);
  }

  function handleSpeedChange(value: string): void {
    const next = Number(value);
    if (Number.isFinite(next)) speed = next;
  }
</script>

<div class="flex items-center gap-2">
  <Button
    variant="outline"
    size="icon"
    class="h-8 w-8 shrink-0"
    onclick={onPlayPause}
    aria-label={playing ? "Pause" : "Play"}
  >
    {#if playing}
      <Pause class="h-4 w-4" />
    {:else}
      <Play class="h-4 w-4" />
    {/if}
  </Button>
  <Button
    variant="outline"
    size="icon"
    class="h-8 w-8 shrink-0"
    onclick={onReset}
    aria-label="Reset"
  >
    <RotateCcw class="h-4 w-4" />
  </Button>

  <Select.Root
    type="single"
    value={String(speed)}
    onValueChange={handleSpeedChange}
  >
    <Select.Trigger class="h-8 w-20 px-2 text-xs" aria-label="Playback speed">
      {speed}x
    </Select.Trigger>
    <Select.Content>
      {#each SPEED_OPTIONS as opt (opt)}
        <Select.Item value={String(opt)} label={`${opt}x`} />
      {/each}
    </Select.Content>
  </Select.Root>

  <!-- Event tick strip. SVG so we can layer playhead + ticks at exact pixel
       positions and react to the same pointer event for click-to-scrub. -->
  <svg
    role="presentation"
    data-testid="playback-tick-strip"
    class="h-8 flex-1 cursor-pointer rounded border bg-muted/20"
    viewBox="0 0 100 32"
    preserveAspectRatio="none"
    onpointerdown={handleStripPointerDown}
  >
    <!-- Max-seen progress fill (so the strip mirrors how far the run got). -->
    <rect
      x="0"
      y="0"
      width={maxPct}
      height="32"
      class="fill-muted/40"
    />
    <!-- Playhead vertical line. -->
    <line
      x1={playPct}
      x2={playPct}
      y1="0"
      y2="32"
      vector-effect="non-scaling-stroke"
      class="stroke-foreground/80"
      stroke-width="1.5"
    />
    {#each tickList as tick, i (`${tick.ruleId ?? "x"}:${tick.tMs}:${i}`)}
      {@const dimmed = tick.xPct > playPct}
      {@const isResolved = tick.kind === AlertReplayEventKind.AutoResolved}
      {@const isSuppressed = tick.kind === AlertReplayEventKind.SuppressedByDnd}
      <line
        data-testid="event-tick"
        data-rule-id={tick.ruleId ?? ""}
        data-kind={tick.kind ?? "fired"}
        x1={tick.xPct}
        x2={tick.xPct}
        y1={isResolved ? "8" : "20"}
        y2="32"
        vector-effect="non-scaling-stroke"
        stroke={severityVar(tick.severity)}
        stroke-width={isSuppressed ? "1.5" : "2"}
        stroke-dasharray={isSuppressed ? "2 2" : null}
        opacity={dimmed ? 0.35 : isSuppressed ? 0.6 : 1}
      />
    {/each}
  </svg>

  <span
    class="font-mono text-xs text-muted-foreground tabular-nums shrink-0 w-32 text-right"
  >
    {currentDate ? formatDateTime(currentDate) : ""}
  </span>
</div>
