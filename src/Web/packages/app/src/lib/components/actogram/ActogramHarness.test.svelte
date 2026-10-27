<script lang="ts">
  import Actogram from "./Actogram.svelte";
  import { MS_PER_HOUR } from "./actogram";
  import { servedDay } from "#lib/test-stubs/actogram-days.js";
  import type { ChartThresholds } from "#lib/constants/glucose-thresholds.js";

  let {
    metric,
    targetLow,
    targetHigh,
  }: {
    metric: "steps" | "heart-rate";
    targetLow: number;
    targetHigh: number;
  } = $props();

  const day = servedDay("2026-10-06");
  const data = [
    { mills: day.startMills + MS_PER_HOUR, value: 80 },
    { mills: day.startMills + 2 * MS_PER_HOUR, value: 100 },
  ];
  const bgData = data.map((point) => ({
    mills: point.mills,
    sgv: 120,
    color: "var(--glucose-in-range)",
  }));
  const thresholds: ChartThresholds = $derived({
    low: 70,
    high: 180,
    veryLow: 54,
    veryHigh: 250,
    glucoseYMax: 300,
    targetLow,
    targetHigh,
  });
</script>

<div class="w-150">
  <Actogram {data} {bgData} days={[day]} {thresholds} rowHeight={64}>
    {#snippet row(ctx)}
      {#each ctx.data as { point, hoursFromStart }, index (index)}
        {@const x = ctx.xScale(new Date(ctx.day.startMills + hoursFromStart * MS_PER_HOUR))}
        {#if metric === "steps"}
          <rect
            data-testid="step-reading"
            {x}
            y={32}
            width={3}
            height={32}
            fill="var(--primary)"
          />
        {:else}
          <circle
            data-testid="heart-rate-reading"
            cx={x}
            cy={ctx.height - point.value / 4}
            r={1.5}
            fill="var(--chart-1)"
          />
        {/if}
      {/each}
    {/snippet}
  </Actogram>
</div>
