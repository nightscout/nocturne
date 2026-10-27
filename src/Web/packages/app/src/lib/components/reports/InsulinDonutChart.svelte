<script lang="ts">
  import { indexBy } from "#lib/utils/collections.js";
  import { formatClock } from "#lib/utils/formatting.js";
  import { PieChart, Text, Tooltip } from "layerchart";
  import type { Bolus, CarbIntake } from "#lib/api/index.js";
  import { patternClass, type TextureKey } from "#lib/components/charts/print/chart-print-patterns.js";
  import * as Breadcrumb from "#lib/components/ui/breadcrumb/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { cn } from "#lib/utils/index.js";
  import ChartKey, { type ChartKeyItem } from "#lib/components/charts/print/ChartKey.svelte";
  import { Key } from "@nocturne/ui/keys";

  interface Props {
    boluses: Bolus[];
    totalBasal: number;
    totalBolus: number;
    manualBolus: number;
    automaticBolus: number;
    scheduledBasal: number;
    additionalBasal: number;
    totalInsulin: number;
    carbIntakes?: CarbIntake[];
    onBolusClick?: (bolus: Bolus) => void;
  }

  let {
    boluses,
    totalBasal,
    totalBolus,
    manualBolus,
    automaticBolus,
    scheduledBasal,
    additionalBasal,
    totalInsulin,
    carbIntakes = [],
    onBolusClick,
  }: Props = $props();

  type Level = "all" | "basal" | "bolus" | "manual";

  const levelTrail: Record<Level, Level[]> = {
    all: ["all"],
    basal: ["all", "basal"],
    bolus: ["all", "bolus"],
    manual: ["all", "bolus", "manual"],
  };

  const levelNames: Record<Level, string> = {
    all: "All",
    basal: "Basal",
    bolus: "Bolus",
    manual: "Manual",
  };

  let level = $state<Level>("all");

  const individualBolusShades = [
    "var(--insulin-bolus)",
    "color-mix(in oklch, var(--insulin-bolus), white 35%)",
  ];

  const carbByCorrelation = $derived(
    indexBy(carbIntakes, (c) => c.correlationId || null, (c) => c)
  );

  interface Slice {
    key: string;
    label: string;
    value: number;
    color: string;
    texture: TextureKey;
    drill?: Level;
    bolus?: Bolus;
    linkedCarbs?: number;
  }

  interface SegmentData extends Slice {
    props: Record<string, unknown>;
  }

  function activate(slice: Slice) {
    if (slice.drill) {
      level = slice.drill;
    } else if (slice.bolus) {
      onBolusClick?.(slice.bolus);
    }
  }

  function arcProps(slice: Slice): Record<string, unknown> {
    const interactive = slice.drill !== undefined || (slice.bolus !== undefined && onBolusClick !== undefined);
    // Per-datum props replace the chart-level arc class instead of merging, so the base class is repeated.
    const props: Record<string, unknown> = {
      class: cn("transition-opacity", patternClass(slice.texture), interactive && "cursor-pointer"),
    };
    if (interactive) {
      props.tabindex = 0;
      props.role = "button";
      props["aria-label"] = `${slice.label} ${slice.value.toFixed(1)}U`;
      props.onkeydown = (e: KeyboardEvent) => {
        if (e.key === Key.Enter || e.key === Key.Space) {
          e.preventDefault();
          activate(slice);
        }
      };
    }
    if (slice.bolus) {
      props.stroke = "var(--background)";
      props["stroke-width"] = 1;
    }
    return props;
  }

  function levelSlices(current: Level): Slice[] {
    switch (current) {
      case "all":
        return [
          { key: "basal", label: "Basal", value: totalBasal, color: "var(--insulin-basal)", texture: "insulin-scheduled-basal", drill: "basal" },
          { key: "bolus", label: "Bolus", value: totalBolus, color: "var(--insulin-bolus)", texture: "insulin-bolus", drill: "bolus" },
        ];
      case "basal":
        return [
          { key: "scheduled-basal", label: "Scheduled basal", value: scheduledBasal, color: "var(--insulin-scheduled-basal)", texture: "insulin-scheduled-basal" },
          { key: "additional-basal", label: "Additional basal", value: additionalBasal, color: "var(--insulin-additional-basal)", texture: "insulin-temp-basal" },
        ];
      case "bolus":
        return [
          { key: "manual-bolus", label: "Manual bolus", value: manualBolus, color: "var(--insulin-bolus)", texture: "insulin-bolus", drill: "manual" },
          { key: "automatic-bolus", label: "Automatic bolus", value: automaticBolus, color: "var(--iob-temporary)", texture: "insulin-automatic-bolus" },
        ];
      case "manual":
        if (boluses.length === 0) {
          return [
            { key: "bolus-0", label: "Manual bolus", value: manualBolus, color: individualBolusShades[0], texture: "insulin-bolus" },
          ];
        }
        return boluses.filter((bolus) => (bolus.insulin ?? 0) > 0).map((bolus, i) => {
          const linkedCarb = bolus.correlationId
            ? carbByCorrelation.get(bolus.correlationId)
            : undefined;
          const time = bolus.mills ? formatClock(bolus.mills) : undefined;
          return {
            key: `bolus-${i}`,
            label: `Bolus${time ? ` @ ${time}` : ""}`,
            value: bolus.insulin ?? 0,
            color: individualBolusShades[i % individualBolusShades.length],
            texture: "insulin-bolus",
            bolus,
            linkedCarbs: linkedCarb?.carbs ?? undefined,
          };
        });
    }
  }

  const segmentData = $derived<SegmentData[]>(
    levelSlices(level)
      .filter((slice) => slice.value > 0)
      .map((slice) => ({ ...slice, props: arcProps(slice) }))
  );

  $effect(() => {
    if (level !== "all" && segmentData.length === 0) {
      level = "all";
    }
  });

  const keyItems = $derived<ChartKeyItem[]>(
    levelSlices(level === "manual" ? "bolus" : level)
      .filter((slice) => slice.value > 0 && (level !== "manual" || slice.key === "manual-bolus"))
      .map((slice) => ({
        texture: slice.texture,
        label: `${slice.label} ${slice.value.toFixed(1)}U`,
      }))
  );

  const levelTotal = $derived.by(() => {
    switch (level) {
      case "all":
        return { label: "Total:", value: totalInsulin };
      case "basal":
        return { label: "Basal:", value: totalBasal };
      case "bolus":
        return { label: "Bolus:", value: totalBolus };
      case "manual":
        return { label: "Manual:", value: manualBolus };
    }
  });

  function handleArcClick(_e: MouseEvent, detail: { data: SegmentData }) {
    activate(detail.data);
  }
</script>

<div class="flex flex-col items-center">
  {#if totalInsulin > 0}
    <Breadcrumb.Root class="mb-2">
      <Breadcrumb.List>
        {#each levelTrail[level] as step, i (step)}
          {#if i > 0}
            <Breadcrumb.Separator />
          {/if}
          <Breadcrumb.Item>
            {#if step === level}
              <Breadcrumb.Page>{levelNames[step]}</Breadcrumb.Page>
            {:else}
              <Button variant="subtle" size="inline-xs" onclick={() => (level = step)}>
                {levelNames[step]}
              </Button>
            {/if}
          </Breadcrumb.Item>
        {/each}
      </Breadcrumb.List>
    </Breadcrumb.Root>
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
        onArcClick={handleArcClick}
        props={{
          arc: {
            class: "transition-opacity",
          },
        }}
      >
        {#snippet aboveMarks()}
          <Text
            value={levelTotal.label}
            textAnchor="middle"
            verticalAnchor="middle"
            dy={-8}
            class="fill-muted-foreground tabular-nums text-xs"
          />
          <Text
            value={`${levelTotal.value.toFixed(1)}U`}
            textAnchor="middle"
            verticalAnchor="middle"
            dy={10}
            class="fill-muted-foreground tabular-nums text-sm font-medium"
          />
        {/snippet}
        {#snippet tooltip(snippetProps)}
          <Tooltip.Root context={snippetProps.context}>
            {#snippet children({ data })}
              {@const d: SegmentData = data}
              <div class="space-y-1 text-sm">
                <div class="font-semibold">{d.label}</div>
                <div class="tabular-nums">{d.value.toFixed(2)}U</div>
                {#if d.linkedCarbs}
                  <div class="text-muted-foreground tabular-nums">
                    {d.linkedCarbs}g carbs
                  </div>
                {/if}
                {#if d.bolus || d.drill}
                  <div
                    class="text-xs text-muted-foreground border-t border-border pt-1 mt-1"
                  >
                    {d.bolus ? "Select to view" : "Select to expand"}
                  </div>
                {/if}
              </div>
            {/snippet}
          </Tooltip.Root>
        {/snippet}
      </PieChart>
    </div>
    <ChartKey class="mt-2" items={keyItems} />
  {:else}
    <div class="h-[140px] w-[140px] flex items-center justify-center">
      <div class="text-2xl font-bold text-muted-foreground">—</div>
    </div>
  {/if}
</div>
