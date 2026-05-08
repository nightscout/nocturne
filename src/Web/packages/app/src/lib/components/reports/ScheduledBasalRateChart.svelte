<script lang="ts">
  import type { ScheduleEntry } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    entries: ScheduleEntry[];
  }

  let { entries }: Props = $props();

  const SECONDS_IN_DAY = 86400;

  function getTimeAsSeconds(entry: ScheduleEntry): number {
    if (entry.timeAsSeconds != null) return entry.timeAsSeconds;
    if (!entry.time) return 0;
    const parts = entry.time.split(":");
    const hours = parseInt(parts[0] ?? "0", 10);
    const minutes = parseInt(parts[1] ?? "0", 10);
    const seconds = parseInt(parts[2] ?? "0", 10);
    return hours * 3600 + minutes * 60 + seconds;
  }

  const chartData = $derived.by(() => {
    if (entries.length === 0) return { steps: [] as { x1: number; x2: number; rate: number }[], maxRate: 0 };

    const sorted = [...entries].sort(
      (a, b) => getTimeAsSeconds(a) - getTimeAsSeconds(b),
    );

    const steps = sorted.map((entry, i) => {
      const startSec = getTimeAsSeconds(entry);
      const endSec =
        i < sorted.length - 1
          ? getTimeAsSeconds(sorted[i + 1]!)
          : SECONDS_IN_DAY;
      return {
        x1: startSec / SECONDS_IN_DAY,
        x2: endSec / SECONDS_IN_DAY,
        rate: entry.value ?? 0,
      };
    });

    const maxRate = Math.max(...steps.map((s) => s.rate), 0.1);

    return { steps, maxRate };
  });

  // Round up to a nice tick value
  function niceMax(val: number): number {
    if (val <= 0.5) return 0.5;
    if (val <= 1) return 1;
    if (val <= 1.5) return 1.5;
    if (val <= 2) return 2;
    if (val <= 2.5) return 2.5;
    if (val <= 3) return 3;
    return Math.ceil(val);
  }

  const yMax = $derived(niceMax(chartData.maxRate));
  const yTicks = $derived.by(() => {
    const mid = yMax / 2;
    return [0, mid, yMax];
  });

  // Build a step-line path from the basal schedule
  const stepPath = $derived.by(() => {
    const steps = chartData.steps;
    if (steps.length === 0) return "";

    const chartW = 365;
    const chartH = 70;
    const toX = (frac: number) => frac * chartW;
    const toY = (rate: number) => chartH - (rate / yMax) * chartH;

    const parts: string[] = [];
    for (const step of steps) {
      const x1 = toX(step.x1);
      const x2 = toX(step.x2);
      const y = toY(step.rate);
      if (parts.length === 0) {
        parts.push(`M${x1},${y}`);
      } else {
        parts.push(`V${y}`);
      }
      parts.push(`H${x2}`);
    }
    return parts.join("");
  });

  // Build a closed fill path (step line closed down to baseline)
  const fillPath = $derived.by(() => {
    const steps = chartData.steps;
    if (steps.length === 0) return "";

    const chartW = 365;
    const chartH = 70;
    const toX = (frac: number) => frac * chartW;
    const toY = (rate: number) => chartH - (rate / yMax) * chartH;

    const parts: string[] = [];
    const firstX = toX(steps[0]!.x1);
    parts.push(`M${firstX},${chartH}`);
    parts.push(`V${toY(steps[0]!.rate)}`);

    for (let i = 0; i < steps.length; i++) {
      const step = steps[i]!;
      const x2 = toX(step.x2);
      const y = toY(step.rate);
      if (i > 0) {
        parts.push(`V${y}`);
      }
      parts.push(`H${x2}`);
    }
    parts.push(`V${chartH}Z`);
    return parts.join("");
  });
</script>

{#if chartData.steps.length > 0}
  <svg viewBox="0 0 400 80" class="w-full h-full">
    <!-- Y axis labels -->
    {#each yTicks as tick}
      {@const y = 75 - (tick / yMax) * 70}
      <text
        x="28"
        {y}
        text-anchor="end"
        class="fill-muted-foreground"
        font-size="8"
        dominant-baseline="middle"
      >
        {tick % 1 === 0 ? tick.toFixed(0) : tick.toFixed(1)}
      </text>
    {/each}

    <!-- Y axis unit -->
    <text
      x="2"
      y="40"
      text-anchor="start"
      class="fill-muted-foreground"
      font-size="7"
      dominant-baseline="middle"
    >
      U
    </text>

    <!-- Chart area -->
    <g transform="translate(32, 5)">
      <!-- Horizontal gridlines -->
      {#each yTicks as tick}
        {@const y = 70 - (tick / yMax) * 70}
        <line
          x1="0"
          y1={y}
          x2="365"
          y2={y}
          class="stroke-border"
          stroke-width="0.5"
          stroke-dasharray="2,2"
        />
      {/each}

      <!-- Filled area under the step line -->
      <path
        d={fillPath}
        style="fill: var(--insulin-scheduled-basal)"
        opacity="0.2"
      />

      <!-- Step line -->
      <path
        d={stepPath}
        fill="none"
        style="stroke: var(--insulin-scheduled-basal)"
        stroke-width="2"
      />

      <!-- Baseline -->
      <line x1="0" y1="70" x2="365" y2="70" class="stroke-border" stroke-width="1" />
    </g>
  </svg>
{:else}
  <div class="flex h-full items-center justify-center text-muted-foreground text-xs">
    No scheduled basal rate data
  </div>
{/if}
