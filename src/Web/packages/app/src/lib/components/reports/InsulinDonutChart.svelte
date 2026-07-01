<script lang="ts">
  import { PieChart, Text, Tooltip } from "layerchart";
  import type { Bolus, CarbIntake } from "$lib/api";
  import { categoryPatternClass } from "$lib/components/charts/print/chart-print-patterns";
  import { cn } from "$lib/utils";

  interface Props {
    boluses: Bolus[];
    scheduledBasal: number;
    additionalBasal: number;
    carbIntakes?: CarbIntake[];
    onBolusClick?: (bolus: Bolus) => void;
  }

  let {
    boluses,
    scheduledBasal,
    additionalBasal,
    carbIntakes = [],
    onBolusClick,
  }: Props = $props();

  function getBolusColor(index: number, total: number): string {
    const mixPercent = Math.round(
      (index / Math.max(1, total - 1)) * 40
    );
    return `color-mix(in oklch, var(--insulin-bolus), white ${mixPercent}%)`;
  }

  // Get boluses with insulin
  const bolusTreatments = $derived(
    boluses.filter((t) => (t.insulin ?? 0) > 0)
  );

  // Build correlation map for tooltip (bolus correlationId -> carb intake)
  const carbByCorrelation = $derived.by(() => {
    const map = new Map<string, CarbIntake>();
    for (const c of carbIntakes) {
      if (c.correlationId) map.set(c.correlationId, c);
    }
    return map;
  });

  // Segment data for the pie chart
  interface SegmentData {
    key: string;
    label: string;
    value: number;
    color: string;
    bolus?: Bolus;
    linkedCarbs?: number;
    time?: string;
    /** Forwarded to the segment's <Arc> (carries the print pattern class). */
    props?: { class: string };
  }

  // Print-pattern slots: bolus, scheduled basal, additional basal are
  // distinguished only by colour, so each gets a distinct mono texture. The
  // base `transition-opacity` is merged in because per-datum props replace the
  // chart-level arc class rather than merging it.
  const baseArcClass = "transition-opacity";
  function arcProps(slot: number): { class: string } {
    return { class: cn(baseArcClass, categoryPatternClass(slot)) };
  }

  const segmentData = $derived.by(() => {
    const segments: SegmentData[] = [];

    bolusTreatments.forEach((t, i) => {
      const linkedCarb = t.correlationId
        ? carbByCorrelation.get(t.correlationId)
        : undefined;

      const time = t.mills
        ? new Date(t.mills).toLocaleTimeString(undefined, {
            hour: "2-digit",
            minute: "2-digit",
          })
        : undefined;

      segments.push({
        key: `bolus-${i}`,
        label: `Bolus${time ? ` @ ${time}` : ""}`,
        value: t.insulin ?? 0,
        color: getBolusColor(i, bolusTreatments.length),
        bolus: t,
        linkedCarbs: linkedCarb?.carbs ?? undefined,
        time,
        props: arcProps(3),
      });
    });

    if (scheduledBasal > 0) {
      segments.push({
        key: "scheduled-basal",
        label: "Scheduled Basal",
        value: scheduledBasal,
        color: "var(--insulin-scheduled-basal)",
        props: arcProps(1),
      });
    }

    if (additionalBasal > 0) {
      segments.push({
        key: "additional-basal",
        label: "Additional Basal",
        value: additionalBasal,
        color: "var(--insulin-additional-basal)",
        props: arcProps(2),
      });
    }

    return segments;
  });

  const totalBolus = $derived(
    bolusTreatments.reduce((sum, t) => sum + (t.insulin ?? 0), 0)
  );
  const total = $derived(totalBolus + scheduledBasal + additionalBasal);

  function handleArcClick(
    _e: MouseEvent,
    detail: { data: SegmentData }
  ) {
    if (detail.data.bolus && onBolusClick) {
      onBolusClick(detail.data.bolus);
    }
  }
</script>

<div class="flex flex-col items-center">
  {#if total > 0}
    <div class="h-[140px] w-[140px]">
      <PieChart
        data={segmentData}
        key="key"
        value="value"
        c="key"
        cRange={segmentData.map((s) => s.color)}
        innerRadius={-30}
        cornerRadius={3}
        padAngle={0.02}
        renderContext={"svg"}
        onArcClick={handleArcClick}
        props={{
          arc: {
            class: "transition-opacity",
          },
        }}
      >
        {#snippet aboveMarks()}
          <Text
            value="Total:"
            textAnchor="middle"
            verticalAnchor="middle"
            dy={-8}
            class="fill-muted-foreground tabular-nums text-xs"
          />
          <Text
            value={`${total.toFixed(1)}U`}
            textAnchor="middle"
            verticalAnchor="middle"
            dy={10}
            class="fill-muted-foreground tabular-nums text-sm font-medium"
          />
        {/snippet}
        {#snippet tooltip(snippetProps)}
          <Tooltip.Root context={snippetProps.context}>
            {#snippet children({ data })}
              {@const d = data as SegmentData}
              <div class="space-y-1 text-sm">
                <div class="font-semibold">{d.label}</div>
                <div class="tabular-nums">{d.value.toFixed(2)}U</div>
                {#if d.linkedCarbs}
                  <div class="text-muted-foreground tabular-nums">
                    {d.linkedCarbs}g carbs
                  </div>
                {/if}
                {#if d.bolus}
                  <div
                    class="text-xs text-muted-foreground border-t border-border pt-1 mt-1"
                  >
                    Click to view
                  </div>
                {/if}
              </div>
            {/snippet}
          </Tooltip.Root>
        {/snippet}
      </PieChart>
    </div>
  {:else}
    <div class="h-[140px] w-[140px] flex items-center justify-center">
      <div class="text-2xl font-bold text-muted-foreground">—</div>
    </div>
  {/if}
</div>
