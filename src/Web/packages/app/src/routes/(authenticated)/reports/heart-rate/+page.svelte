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
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_HOUR } from "$lib/components/actogram/actogram";
  import { getActogramData } from "$api/actogram.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  const VISIBLE_DAYS = 14;
  const PADDING_DAYS = 14;
  const MS_PER_DAY = 24 * 60 * 60 * 1000;

  const reportsParams = requireDateParamsContext(14);

  const dateRangeMillis = $derived({
    from: new Date(
      reportsParams.dateRangeInput.from ?? new Date().toISOString()
    ).getTime() - PADDING_DAYS * MS_PER_DAY,
    to: new Date(
      reportsParams.dateRangeInput.to ?? new Date().toISOString()
    ).getTime() + PADDING_DAYS * MS_PER_DAY,
  });

  const actogramResource = contextResource(
    () =>
      getActogramData({
        from: dateRangeMillis.from,
        to: dateRangeMillis.to,
      }),
    { errorTitle: "Error Loading Heart Rate Report" }
  );

  const heartRates = $derived(actogramResource.current?.heartRates ?? []);
  const glucoseData = $derived(actogramResource.current?.glucoseData ?? []);
  const thresholds = $derived(actogramResource.current?.thresholds);

  // Build day array from date range
  const days = $derived.by(() => {
    const start = new Date(dateRangeMillis.from);
    const end = new Date(dateRangeMillis.to);
    const startMidnight = new Date(
      start.getFullYear(),
      start.getMonth(),
      start.getDate()
    );
    const endMidnight = new Date(
      end.getFullYear(),
      end.getMonth(),
      end.getDate()
    );
    const dayCount =
      Math.round(
        (endMidnight.getTime() - startMidnight.getTime()) /
          (24 * 60 * 60 * 1000)
      ) + 1;
    return Array.from({ length: dayCount }, (_, i) => {
      const d = new Date(startMidnight);
      d.setDate(d.getDate() + i);
      return d;
    });
  });

  // HR data as ActogramPoints
  const hrPoints = $derived(
    heartRates.map((h) => ({ mills: h.mills, bpm: h.bpm }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    glucoseData.map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics
  const avgBpm = $derived(
    heartRates.length > 0
      ? Math.round(
          heartRates.reduce((sum, h) => sum + h.bpm, 0) / heartRates.length
        )
      : 0
  );
  const minBpm = $derived(
    heartRates.length > 0 ? Math.min(...heartRates.map((h) => h.bpm)) : 0
  );
  const maxBpm = $derived(
    heartRates.length > 0 ? Math.max(...heartRates.map((h) => h.bpm)) : 0
  );

  // Resting HR estimate: 10th percentile of all readings
  const restingBpm = $derived.by(() => {
    if (heartRates.length === 0) return 0;
    const sorted = [...heartRates].sort((a, b) => a.bpm - b.bpm);
    const idx = Math.floor(sorted.length * 0.1);
    return sorted[idx]?.bpm ?? 0;
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

{#if actogramResource.current}
  <div class="container mx-auto space-y-6 px-4 py-6 max-w-7xl">
    <!-- Header -->
    <div>
      <h1 class="text-3xl font-bold">Heart Rate</h1>
      <p class="text-muted-foreground">
        Daily heart rate patterns with glucose overlay
      </p>
    </div>

    <!-- Summary Cards -->
    <div class="grid grid-cols-2 sm:grid-cols-4 gap-4">
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
              {heartRates.length.toLocaleString()}
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
      <CardContent>
        <Actogram
          data={hrPoints}
          bgData={bgPoints}
          {days}
          {thresholds}
          rowHeight={48}
          visibleCount={VISIBLE_DAYS}
          initialOffset={PADDING_DAYS}
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
{/if}
