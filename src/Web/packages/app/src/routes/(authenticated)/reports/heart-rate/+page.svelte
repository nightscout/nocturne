<script lang="ts">
  import { formatNumber } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import HeartPulse from "@lucide/svelte/icons/heart-pulse";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import {
    Actogram,
    type ActogramRowContext,
  } from "#lib/components/actogram/index.js";
  import { MS_PER_HOUR } from "#lib/components/actogram/actogram.js";
  import { useActogramReport } from "#lib/hooks/actogram-report.svelte.js";
  import { PrintMode } from "#lib/components/charts/print/print-mode.svelte.js";

  const VISIBLE_DAYS = 14;
  const print = new PrintMode();

  const report = useActogramReport("Error loading heart rate report");
  const { resource: actogramResource } = report;

  const noZoneReason = $derived(actogramResource.current?.timeZoneUnavailableReason);

  // HR data as ActogramPoints
  const hrPoints = $derived(
    (actogramResource.current?.heartRates ?? []).map((h) => ({ mills: h.mills, bpm: h.bpm }))
  );

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  const hrSummary = $derived(actogramResource.current?.heartRateSummary);
  const minBpm = $derived(hrSummary?.minBpm ?? 0);
  const maxBpm = $derived(hrSummary?.maxBpm ?? 0);

  // Scale for dot Y position (map BPM to row height)
  const bpmMin = $derived(Math.max(30, minBpm - 10));
  const bpmMax = $derived(Math.min(220, maxBpm + 10));
</script>

<div class="@container space-y-6">
  <div class="print:hidden">
    <p class="text-muted-foreground">
      Daily heart rate patterns with glucose overlay
    </p>
  </div>

  <FigureStrip
    figures={[
      { label: "Average", value: hrSummary ? String(hrSummary.averageBpm ?? 0) : "--", unit: "bpm" },
      { label: "Resting estimate", value: hrSummary ? String(hrSummary.restingBpm ?? 0) : "--", unit: "bpm" },
      { label: "Min / max", value: hrSummary ? `${minBpm} / ${maxBpm}` : "--", unit: "bpm" },
      { label: "Readings", value: formatNumber(hrSummary?.readingCount ?? 0) },
    ]}
  />

  {#if noZoneReason}
    <p class="text-sm text-muted-foreground">
      <TimeZoneNote reason={noZoneReason}>The rows use UTC days.</TimeZoneNote>
    </p>
  {/if}

  <Card class="print:break-inside-auto!">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <HeartPulse class="h-5 w-5 text-heart-rate" />
        Heart rate actogram
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
        printCount={report.rangeDayCount}
        initialOffset={0}
        legend={[
          {
            texture: "heart-rate",
            label: "Heart rate",
            shape: "dot",
            // The dots print in ink.
            color: print.active ? "var(--foreground)" : undefined,
          },
        ]}
      >
        {#snippet tooltipValue({ point })}
          {@const bpm = typeof point.bpm === "number" ? point.bpm : 0}
          <span class="text-muted-foreground">Heart rate</span>
          <span class="ml-auto font-medium tabular-nums">{bpm} bpm</span>
        {/snippet}
        {#snippet row(ctx: ActogramRowContext)}
          {#each ctx.data as { point, hoursFromStart, isExtended }, i (i)}
            {@const bpm = typeof point.bpm === "number" ? point.bpm : 0}
            {@const yNorm = (bpm - bpmMin) / (bpmMax - bpmMin)}
            {@const y = ctx.height - yNorm * ctx.height}
            {@const x = ctx.xScale(new Date(ctx.day.startMills + hoursFromStart * MS_PER_HOUR))}
            <circle
              cx={x}
              cy={y}
              r={1.5}
              fill="var(--chart-1)"
              opacity={isExtended ? 0.3 : 0.7}
              class={isExtended ? "print:[r:2px]" : "print:fill-foreground print:opacity-80 print:[r:1.75px]"}
            />
          {/each}
        {/snippet}
      </Actogram>
    </CardContent>
  </Card>
</div>
