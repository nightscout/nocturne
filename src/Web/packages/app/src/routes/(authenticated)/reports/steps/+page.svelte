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
  import { computeDayTotals, computeInitialOffset } from './steps.utils';
  import { untrack } from 'svelte';

  const VISIBLE_DAYS = 14;
  const PADDING_DAYS = 14;
  const MS_PER_DAY = 24 * 60 * 60 * 1000;

  const reportsParams = requireDateParamsContext(14);

  // Frozen at mount — does NOT react to reportsParams changes caused by actogram navigation.
  // This prevents a re-fetch every time the user scrolls the actogram.
  const fetchRange = {
    from: reportsParams.dateRangeMillis.from - PADDING_DAYS * MS_PER_DAY,
    to: reportsParams.dateRangeMillis.to + PADDING_DAYS * MS_PER_DAY,
  };

  const actogramResource = contextResource(
    () =>
      getActogramData({
        from: fetchRange.from,
        to: fetchRange.to,
      }),
    { errorTitle: "Error Loading Step Count Report" }
  );

  // Build day array from date range
  const days = $derived.by(() => {
    const start = new Date(fetchRange.from);
    const end = new Date(fetchRange.to);
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

  const dayTotals = $derived(computeDayTotals(actogramResource.current?.stepCounts ?? [], days));

  function formatDate(date: Date): string {
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  // Read once at mount — not reactive. Sets the starting scroll position from URL params.
  // Uses local midnight from the days array to avoid UTC/local timezone mismatch.
  const initialOffset = untrack(() => {
    const targetDate = reportsParams.from;
    const targetMs = targetDate
      ? new Date(targetDate).setHours(0, 0, 0, 0)
      : fetchRange.from;
    return computeInitialOffset(days, targetMs, VISIBLE_DAYS);
  });

  // Step data as ActogramPoints
  const stepPoints = $derived(
    (actogramResource.current?.stepCounts ?? []).map((s) => ({ mills: s.mills, steps: s.metric }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics scoped to the currently visible period (not the padded fetch window)
  const visibleStepCounts = $derived(
    (actogramResource.current?.stepCounts ?? []).filter(
      (s) =>
        s.mills >= reportsParams.dateRangeMillis.from &&
        s.mills <= reportsParams.dateRangeMillis.to + MS_PER_DAY - 1
    )
  );
  const totalSteps = $derived(visibleStepCounts.reduce((sum, s) => sum + s.metric, 0));
  const dayCount = $derived(
    reportsParams.from && reportsParams.to
      ? Math.round(
          (new Date(reportsParams.to).getTime() - new Date(reportsParams.from).getTime()) /
            MS_PER_DAY
        ) + 1
      : VISIBLE_DAYS
  );
  const dailyAverage = $derived(
    dayCount > 0 ? Math.round(totalSteps / dayCount) : 0
  );
  const maxStepsInHour = $derived.by(() => {
    const counts = actogramResource.current?.stepCounts ?? [];
    return counts.length > 0 ? Math.max(...counts.map((s) => s.metric)) : 1000;
  });
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

{#await actogramResource then actogramData}
  {#if actogramData}
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
          thresholds={actogramResource.current?.thresholds}
          rowHeight={64}
          visibleCount={VISIBLE_DAYS}
          {initialOffset}
          onVisibleRangeChange={(from, to) => {
            reportsParams.setCustomRange(
              `${from.getFullYear()}-${String(from.getMonth() + 1).padStart(2, '0')}-${String(from.getDate()).padStart(2, '0')}`,
              `${to.getFullYear()}-${String(to.getMonth() + 1).padStart(2, '0')}-${String(to.getDate()).padStart(2, '0')}`,
            );
          }}
        >
          {#snippet rowLabel({ day })}
            <div class="text-right pr-2">
              <div class="text-xs text-muted-foreground">{formatDate(day)}</div>
              <div class="text-xs font-medium tabular-nums">
                {(dayTotals.get(day.getTime()) ?? 0).toLocaleString()} <span class="text-muted-foreground font-normal">steps</span>
              </div>
            </div>
          {/snippet}
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
{/await}
