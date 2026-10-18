<script lang="ts">
  import Actogram from "./Actogram.svelte";
  import { MS_PER_HOUR, type GlucoseThresholds } from "./actogram";

  let {
    metric,
    targetLow,
    targetHigh,
  }: {
    metric: "steps" | "heart-rate";
    targetLow: number;
    targetHigh: number;
  } = $props();

  const day = new Date("2026-10-06T00:00:00Z");
  const data = [
    { mills: day.getTime() + MS_PER_HOUR, value: 80 },
    { mills: day.getTime() + 2 * MS_PER_HOUR, value: 100 },
  ];
  const bgData = data.map((point) => ({
    mills: point.mills,
    sgv: 120,
    color: "var(--glucose-in-range)",
  }));
  const thresholds: GlucoseThresholds = $derived({
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
        {@const x = ctx.xScale(new Date(ctx.day.getTime() + hoursFromStart * MS_PER_HOUR))}
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
