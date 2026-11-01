<script lang="ts">
  import { formatNumber } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import Footprints from "@lucide/svelte/icons/footprints";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import {
    Actogram,
    extentOf,
    formatActogramDay,
    pointsInRange,
    type ActogramRowContext,
  } from "#lib/components/actogram/index.js";
  import { MS_PER_HOUR } from "#lib/components/actogram/actogram.js";
  import { useActogramReport } from "#lib/hooks/actogram-report.svelte.js";

  const VISIBLE_DAYS = 14;

  const report = useActogramReport("Error loading step count report");
  const { resource: actogramResource } = report;

  const dayTotals = $derived(actogramResource.current?.stepDayTotals ?? {});
  const noZoneReason = $derived(actogramResource.current?.timeZoneUnavailableReason);

  // Step data as ActogramPoints
  const stepPoints = $derived(
    (actogramResource.current?.stepCounts ?? []).map((s) => ({ mills: s.mills, steps: s.metric }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  const stepSummary = $derived(actogramResource.current?.stepSummary);
  // Bar heights scale to the selected range's largest sample, not the padded fetch window
  const barScale = $derived.by(() => {
    const range = actogramResource.current?.range;
    const stepCounts = actogramResource.current?.stepCounts ?? [];
    const inRange = range ? pointsInRange(stepCounts, range.from, range.to) : [];
    return Math.max(extentOf(inRange, (s) => s.metric)?.max ?? 0, 1000);
  });
</script>

<div class="@container space-y-6">
  <div class="print:hidden">
    <p class="text-muted-foreground">
      Daily step patterns with glucose overlay
    </p>
  </div>

  <FigureStrip
    figures={[
      { label: "Total steps", value: stepSummary ? formatNumber(stepSummary.totalSteps ?? 0) : "--", unit: "steps" },
      { label: "Daily average", value: stepSummary ? formatNumber(stepSummary.dailyAverage ?? 0) : "--", unit: "steps/day" },
      { label: "Period", value: stepSummary ? String(stepSummary.dayCount ?? 0) : "--", unit: "days" },
    ]}
  />

  {#if noZoneReason}
    <p class="text-sm text-muted-foreground">
      <TimeZoneNote reason={noZoneReason}>
        The rows and their step totals use UTC days.
      </TimeZoneNote>
    </p>
  {/if}

  <Card class="print:break-inside-auto!">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Footprints class="h-5 w-5 text-muted-foreground" />
        Step count actogram
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
            <div class="text-xs text-muted-foreground">{formatActogramDay(day)}</div>
            <div class="text-xs font-medium tabular-nums" data-testid="day-total">
              {formatNumber(dayTotals[day.date])} <span class="text-muted-foreground font-normal @max-md/actogram:sr-only">steps</span>
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
            {@const x = ctx.xScale(new Date(ctx.day.startMills + hoursFromStart * MS_PER_HOUR))}
            <rect
              data-testid="step-reading"
              data-steps={steps}
              data-extended={isExtended}
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
