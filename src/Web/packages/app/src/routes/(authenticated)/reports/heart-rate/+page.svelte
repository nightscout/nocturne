<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { HeartPulse, TrendingDown, TrendingUp, Calendar } from "lucide-svelte";
  import {
    Actogram,
    extentOf,
    pointsInRange,
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_HOUR } from "$lib/components/actogram/actogram";
  import { useActogramReport } from "$lib/hooks/actogram-report.svelte";

  const VISIBLE_DAYS = 14;

  const report = useActogramReport("Error Loading Heart Rate Report");
  const { params: reportsParams, resource: actogramResource } = report;

  // HR data as ActogramPoints
  const hrPoints = $derived(
    (actogramResource.current?.heartRates ?? []).map((h) => ({ mills: h.mills, bpm: h.bpm }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics cover the selected range only. The actogram fetches ±14
  // days of context around it, so aggregating the whole response described a
  // window up to 28 days wider than the one the picker showed.
  const selectedRates = $derived(
    pointsInRange(
      actogramResource.current?.heartRates ?? [],
      reportsParams.dateRangeMillis.from,
      reportsParams.dateRangeMillis.to
    )
  );

  const avgBpm = $derived(
    selectedRates.length > 0
      ? Math.round(selectedRates.reduce((sum, h) => sum + h.bpm, 0) / selectedRates.length)
      : 0
  );
  const bpmExtent = $derived(extentOf(selectedRates, (h) => h.bpm));
  const minBpm = $derived(bpmExtent?.min ?? 0);
  const maxBpm = $derived(bpmExtent?.max ?? 0);

  // Resting HR estimate: 10th percentile of the range's readings
  const restingBpm = $derived.by(() => {
    if (selectedRates.length === 0) return 0;
    const sorted = [...selectedRates].sort((a, b) => a.bpm - b.bpm);
    return sorted[Math.floor(sorted.length * 0.1)]?.bpm ?? 0;
  });

  // Scale for dot Y position (map BPM to row height)
  const bpmMin = $derived(Math.max(30, minBpm - 10));
  const bpmMax = $derived(Math.min(220, maxBpm + 10));
</script>

<svelte:head>
  <title>Heart Rate - Nocturne Reports</title>
  <meta
    name="description"
    content="Heart rate actogram with glucose overlay"
  />
</svelte:head>

<div class="@container container mx-auto space-y-6 p-3 @md:p-6 max-w-7xl">
  <!-- Header -->
  <div>
    <h1 class="text-2xl @md:text-3xl font-bold">Heart Rate</h1>
    <p class="text-muted-foreground">
      Daily heart rate patterns with glucose overlay
    </p>
  </div>

  <!-- Summary Cards -->
  <div class="grid grid-cols-2 @sm:grid-cols-4 gap-4">
    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium text-muted-foreground">
          Average
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div class="flex items-center gap-2">
          <HeartPulse class="h-5 w-5 text-red-500" />
          <span class="text-2xl font-bold tabular-nums">{avgBpm}</span>
          <span class="text-sm text-muted-foreground">bpm</span>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium text-muted-foreground">
          Resting Estimate
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div class="flex items-center gap-2">
          <TrendingDown class="h-5 w-5 text-blue-500" />
          <span class="text-2xl font-bold tabular-nums">{restingBpm}</span>
          <span class="text-sm text-muted-foreground">bpm</span>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium text-muted-foreground">
          Min / Max
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div class="flex items-center gap-2">
          <TrendingUp class="h-5 w-5 text-muted-foreground" />
          <span class="text-2xl font-bold tabular-nums">
            {minBpm}<span class="text-muted-foreground font-normal">/</span>{maxBpm}
          </span>
          <span class="text-sm text-muted-foreground">bpm</span>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium text-muted-foreground">
          Readings
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div class="flex items-center gap-2">
          <Calendar class="h-5 w-5 text-muted-foreground" />
          <span class="text-2xl font-bold tabular-nums">
            {selectedRates.length.toLocaleString()}
          </span>
        </div>
      </CardContent>
    </Card>
  </div>

  <!-- Actogram -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <HeartPulse class="h-5 w-5 text-red-500" />
        Heart Rate Actogram
      </CardTitle>
    </CardHeader>
    <CardContent class="w-full overflow-x-auto print:overflow-visible">
      <Actogram
        data={hrPoints}
        bgData={bgPoints}
        days={report.days}
        thresholds={actogramResource.current?.thresholds}
        rowHeight={48}
        visibleCount={VISIBLE_DAYS}
        initialOffset={0}
      >
        {#snippet tooltipValue({ point })}
          {@const bpm = (point as { mills: number; bpm: number }).bpm ?? 0}
          <span class="text-muted-foreground">Heart Rate</span>
          <span class="ml-auto font-mono font-medium tabular-nums">{bpm} bpm</span>
        {/snippet}
        {#snippet row(ctx: ActogramRowContext)}
          {#each ctx.data as { point, hoursFromStart, isExtended }}
            {@const bpm = (point as { mills: number; bpm: number }).bpm ?? 0}
            {@const yNorm = (bpm - bpmMin) / (bpmMax - bpmMin)}
            {@const y = ctx.height - yNorm * ctx.height}
            {@const x = ctx.xScale(new Date(ctx.day.getTime() + hoursFromStart * MS_PER_HOUR))}
            <circle
              cx={x}
              cy={y}
              r={1.5}
              fill="var(--chart-1)"
              opacity={isExtended ? 0.3 : 0.7}
            />
          {/each}
        {/snippet}
      </Actogram>
    </CardContent>
  </Card>
</div>
