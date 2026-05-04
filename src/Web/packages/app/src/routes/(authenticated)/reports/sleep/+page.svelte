<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Moon, Clock, Calendar } from "lucide-svelte";
  import {
    Actogram,
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_HOUR, HOURS_PER_ROW } from "$lib/components/actogram/actogram";
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
    { errorTitle: "Error Loading Sleep Report" }
  );

  const sleepSpans = $derived(actogramResource.current?.sleepSpans ?? []);
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

  // Convert sleep spans into ActogramPoints (use midpoint of each span)
  // Each point carries startMills and endMills for rectangle rendering
  const sleepPoints = $derived(
    sleepSpans.map((s) => ({
      mills: s.startMills,
      startMills: s.startMills,
      endMills: s.endMills,
      state: s.state,
    }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    glucoseData.map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics
  const totalSleepMs = $derived(
    sleepSpans.reduce((sum, s) => sum + (s.endMills - s.startMills), 0)
  );
  const avgSleepHours = $derived(
    days.length > 0
      ? totalSleepMs / days.length / (1000 * 60 * 60)
      : 0
  );
  const totalNights = $derived(sleepSpans.length);

  function formatHoursMinutes(hours: number): string {
    const h = Math.floor(hours);
    const m = Math.round((hours - h) * 60);
    return `${h}h ${m}m`;
  }

  // Color for sleep state
  function getSleepColor(state: string): string {
    const lower = state.toLowerCase();
    if (lower.includes("deep")) return "var(--chart-3)";
    if (lower.includes("rem")) return "var(--chart-2)";
    if (lower.includes("light")) return "var(--chart-1)";
    // Default sleep color
    return "var(--chart-1)";
  }

</script>

<svelte:head>
  <title>Sleep & Overnight - Nocturne Reports</title>
  <meta
    name="description"
    content="Sleep pattern actogram with glucose overlay"
  />
</svelte:head>

{#if actogramResource.current}
  <div class="container mx-auto space-y-6 px-4 py-6 max-w-7xl">
    <!-- Header -->
    <div>
      <h1 class="text-3xl font-bold">Sleep & Overnight</h1>
      <p class="text-muted-foreground">
        Sleep patterns with overnight glucose overlay
      </p>
    </div>

    <!-- Summary Cards -->
    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-sm font-medium text-muted-foreground">
            Average Sleep
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="flex items-center gap-2">
            <Moon class="h-5 w-5 text-indigo-500" />
            <span class="text-2xl font-bold tabular-nums">
              {formatHoursMinutes(avgSleepHours)}
            </span>
            <span class="text-sm text-muted-foreground">per night</span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-sm font-medium text-muted-foreground">
            Recorded Nights
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="flex items-center gap-2">
            <Clock class="h-5 w-5 text-muted-foreground" />
            <span class="text-2xl font-bold tabular-nums">{totalNights}</span>
            <span class="text-sm text-muted-foreground">sessions</span>
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
            <span class="text-2xl font-bold tabular-nums">{days.length}</span>
            <span class="text-sm text-muted-foreground">days</span>
          </div>
        </CardContent>
      </Card>
    </div>

    <!-- Actogram -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Moon class="h-5 w-5 text-indigo-500" />
          Sleep Actogram
        </CardTitle>
      </CardHeader>
      <CardContent>
        <Actogram
          data={sleepPoints}
          bgData={bgPoints}
          {days}
          {thresholds}
          rowHeight={48}
          visibleCount={VISIBLE_DAYS}
          initialOffset={PADDING_DAYS}
        >
          {#snippet tooltipValue({ point })}
            {@const span = point as { mills: number; state: string }}
            <div class="size-2 rounded-full" style:background={getSleepColor(span.state)}></div>
            <span class="text-muted-foreground">Sleep</span>
            <span class="ml-auto font-mono font-medium tabular-nums capitalize">{span.state.toLowerCase()}</span>
          {/snippet}
          {#snippet row(ctx: ActogramRowContext)}
            {#each ctx.data as { point, hoursFromStart, isExtended }}
              {@const span = point as { mills: number; startMills: number; endMills: number; state: string }}
              {@const durationHours = (span.endMills - span.startMills) / MS_PER_HOUR}
              {@const x = ctx.xScale(new Date(ctx.day.getTime() + hoursFromStart * MS_PER_HOUR))}
              {@const endHours = hoursFromStart + durationHours}
              {@const clampedEnd = Math.min(endHours, HOURS_PER_ROW)}
              {@const x2 = ctx.xScale(new Date(ctx.day.getTime() + clampedEnd * MS_PER_HOUR))}
              {@const rectWidth = Math.max(x2 - x, 1)}
              <rect
                {x}
                y={4}
                width={rectWidth}
                height={ctx.height - 8}
                fill={getSleepColor(span.state)}
                opacity={isExtended ? 0.25 : 0.5}
                rx={2}
              />
            {/each}
          {/snippet}
        </Actogram>
      </CardContent>
    </Card>
  </div>
{/if}
