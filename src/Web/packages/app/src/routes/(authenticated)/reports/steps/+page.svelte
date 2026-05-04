<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Footprints, TrendingUp, Calendar } from "lucide-svelte";
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
    { errorTitle: "Error Loading Step Count Report" }
  );

  const stepCounts = $derived(actogramResource.current?.stepCounts ?? []);
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

  // Step data as ActogramPoints
  const stepPoints = $derived(
    stepCounts.map((s) => ({ mills: s.mills, steps: s.metric }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    glucoseData.map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics
  const totalSteps = $derived(
    stepCounts.reduce((sum, s) => sum + s.metric, 0)
  );
  const dayCount = $derived(days.length);
  const dailyAverage = $derived(
    dayCount > 0 ? Math.round(totalSteps / dayCount) : 0
  );
  const maxStepsInHour = $derived(
    stepCounts.length > 0
      ? Math.max(...stepCounts.map((s) => s.metric))
      : 1000
  );
  // Cap bar scale at a reasonable max
  const barScale = $derived(Math.max(maxStepsInHour, 1000));
</script>

<svelte:head>
  <title>Step Count - Nocturne Reports</title>
  <meta
    name="description"
    content="Step count actogram with glucose overlay"
  />
</svelte:head>

{#if actogramResource.current}
  <div class="container mx-auto space-y-6 px-4 py-6 max-w-7xl">
    <!-- Header -->
    <div>
      <h1 class="text-3xl font-bold">Step Count</h1>
      <p class="text-muted-foreground">
        Daily step patterns with glucose overlay
      </p>
    </div>

    <!-- Summary Cards -->
    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-sm font-medium text-muted-foreground">
            Total Steps
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="flex items-center gap-2">
            <Footprints class="h-5 w-5 text-primary" />
            <span class="text-2xl font-bold tabular-nums">
              {totalSteps.toLocaleString()}
            </span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-sm font-medium text-muted-foreground">
            Daily Average
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="flex items-center gap-2">
            <TrendingUp class="h-5 w-5 text-primary" />
            <span class="text-2xl font-bold tabular-nums">
              {dailyAverage.toLocaleString()}
            </span>
            <span class="text-sm text-muted-foreground">steps/day</span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-sm font-medium text-muted-foreground">
            Period
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="flex items-center gap-2">
            <Calendar class="h-5 w-5 text-muted-foreground" />
            <span class="text-2xl font-bold tabular-nums">{dayCount}</span>
            <span class="text-sm text-muted-foreground">days</span>
          </div>
        </CardContent>
      </Card>
    </div>

    <!-- Actogram -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Footprints class="h-5 w-5 text-muted-foreground" />
          Step Count Actogram
        </CardTitle>
      </CardHeader>
      <CardContent>
        <Actogram
          data={stepPoints}
          bgData={bgPoints}
          {days}
          {thresholds}
          rowHeight={48}
          visibleCount={VISIBLE_DAYS}
          initialOffset={PADDING_DAYS}
        >
          {#snippet tooltipValue({ point })}
            {@const steps = (point as { mills: number; steps: number }).steps ?? 0}
            <span class="text-muted-foreground">Steps</span>
            <span class="ml-auto font-mono font-medium tabular-nums">{steps.toLocaleString()}</span>
          {/snippet}
          {#snippet row(ctx: ActogramRowContext)}
            {#each ctx.data as { point, hoursFromStart, isExtended }}
              {@const steps = (point as { mills: number; steps: number }).steps ?? 0}
              {@const barHeight = (steps / barScale) * ctx.height}
              {@const x = ctx.xScale(new Date(ctx.day.getTime() + hoursFromStart * MS_PER_HOUR))}
              <rect
                {x}
                y={ctx.height - barHeight}
                width={3}
                height={barHeight}
                fill="var(--primary)"
                opacity={isExtended ? 0.35 : 0.8}
              />
            {/each}
          {/snippet}
        </Actogram>
      </CardContent>
    </Card>
  </div>
{/if}
