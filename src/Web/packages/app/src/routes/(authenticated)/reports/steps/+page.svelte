<script lang="ts">
  import { formatNumber, formatShortDate } from "$lib/utils/formatting";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Footprints } from "lucide-svelte";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import {
    Actogram,
    extentOf,
    pointsInRange,
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_HOUR } from "$lib/components/actogram/actogram";
  import { useActogramReport } from "$lib/hooks/actogram-report.svelte";
  import { toDayString } from "$lib/utils/date-range";

  const VISIBLE_DAYS = 14;

  const report = useActogramReport("Error Loading Step Count Report");
  const { params: reportsParams, resource: actogramResource } = report;

  const dayTotals = $derived(actogramResource.current?.stepDayTotals ?? {});

  function formatDate(date: Date): string {
    return formatShortDate(date);
  }

  // Step data as ActogramPoints
  const stepPoints = $derived(
    (actogramResource.current?.stepCounts ?? []).map((s) => ({ mills: s.mills, steps: s.metric }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  // Summary statistics scoped to the selected range, not the padded fetch window
  const selectedStepCounts = $derived(
    pointsInRange(
      actogramResource.current?.stepCounts ?? [],
      reportsParams.dateRangeMillis.from,
      reportsParams.dateRangeMillis.to
    )
  );
  const totalSteps = $derived(selectedStepCounts.reduce((sum, s) => sum + s.metric, 0));
  const dayCount = $derived(reportsParams.dayCount);
  const dailyAverage = $derived(Math.round(totalSteps / dayCount));
  // Cap bar scale at a reasonable max
  const barScale = $derived(
    Math.max(extentOf(selectedStepCounts, (s) => s.metric)?.max ?? 0, 1000)
  );
</script>

<svelte:head>
  <title>Step Count - Nocturne Reports</title>
  <meta
    name="description"
    content="Step count actogram with glucose overlay"
  />
</svelte:head>

<div class="@container container mx-auto space-y-6 p-3 @md:p-6 max-w-7xl">
  <div class="print:hidden">
    <h1 class="text-2xl @md:text-3xl font-bold">Step Count</h1>
    <p class="text-muted-foreground">
      Daily step patterns with glucose overlay
    </p>
  </div>

  <FigureStrip
    figures={[
      { label: "Total steps", value: formatNumber(totalSteps), unit: "steps" },
      { label: "Daily average", value: formatNumber(dailyAverage), unit: "steps/day" },
      { label: "Period", value: String(dayCount), unit: "days" },
    ]}
  />

  <Card class="print:break-inside-auto!">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Footprints class="h-5 w-5 text-muted-foreground" />
        Step Count Actogram
      </CardTitle>
    </CardHeader>
    <CardContent class="w-full overflow-x-auto print:overflow-visible">
      <Actogram
        data={stepPoints}
        bgData={bgPoints}
        days={report.days}
        thresholds={actogramResource.current?.thresholds}
        rowHeight={64}
        visibleCount={VISIBLE_DAYS}
        printCount={report.rangeDayCount}
        legend={[{ texture: "steps", label: "Steps" }]}
      >
        {#snippet rowLabel({ day })}
          <div class="text-right pr-2">
            <div class="text-xs text-muted-foreground">{formatDate(day)}</div>
            <div class="text-xs font-medium tabular-nums">
              {formatNumber(dayTotals[toDayString(day)])} <span class="text-muted-foreground font-normal">steps</span>
            </div>
          </div>
        {/snippet}
        {#snippet tooltipValue({ point })}
          {@const steps = typeof point.steps === "number" ? point.steps : 0}
          <span class="text-muted-foreground">Steps</span>
          <span class="ml-auto font-medium tabular-nums">{formatNumber(steps)}</span>
        {/snippet}
        {#snippet row(ctx: ActogramRowContext)}
          {#each ctx.data as { point, hoursFromStart, isExtended }, i (i)}
            {@const steps = typeof point.steps === "number" ? point.steps : 0}
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
