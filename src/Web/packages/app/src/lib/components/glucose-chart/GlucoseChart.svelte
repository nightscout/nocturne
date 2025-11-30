<script lang="ts">
  import { LineChart, ScatterChart } from "layerchart";
  import { scaleTime, scaleThreshold, scaleLinear } from "d3-scale";
  import { chartConfig, TIR_COLORS_CSS } from "$lib/constants";
  import * as ChartC from "$lib/components/ui/chart/index.js";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import type { Entry, GlycemicThresholds, Treatment } from "$lib/api";
  import { cn, type WithElementRef } from "$lib/utils";
  import type { HTMLAttributes } from "svelte/elements";
  import type { DateRange } from "@layerstack/utils/dateRange";

  interface Props {
    entries: Entry[];
    treatments: Treatment[];
    /** Optional. If not provided, will be inferred from the supplied entries. */
    dateRange?: DateRange;
    thresholds?: GlycemicThresholds;
  }

  let {
    // Forwarded element ref for consumers if needed
    ref = $bindable(null),
    // Allow consumers to provide custom root classes using the `class` attribute
    class: className,
    entries,
    treatments,
    dateRange,
    thresholds = DEFAULT_THRESHOLDS,
    // Collect any additional props so they can be forwarded to the root element
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLElement>> & Props = $props();

  // Resolve the effective date range – if not explicitly provided, infer from the entries.
  const resolvedDateRange: DateRange = $derived(
    dateRange
      ? dateRange
      : (() => {
          const times = entries
            .map((e) => {
              const d = getDateString(e);
              return d ? d.getTime() : undefined;
            })
            .filter((t): t is number => t !== undefined);

          if (times.length === 0) {
            const nowIso = new Date();
            return { from: nowIso, to: nowIso };
          }

          const startIso = new Date(Math.min(...times));
          const endIso = new Date(Math.max(...times));

          return { from: startIso, to: endIso };
        })()
  );

  const insulinToCarbRatio = 12; // Hardcoded ratio

  // Create D3 scale for insulin to carb ratio
  const insulinScale = scaleLinear()
    .domain([0, 1]) // 1 unit of insulin
    .range([0, insulinToCarbRatio]); // maps to 12 carbs

  // Scale insulin values by the insulin-to-carb ratio using D3 scale
  const scaledTreatments = $derived(
    treatments.map((treatment) => ({
      ...treatment,
      insulin: treatment.insulin
        ? insulinScale(treatment.insulin)
        : treatment.insulin,
    }))
  );

  const xScale = $derived(
    scaleTime().domain([
      resolvedDateRange.from ?? new Date(),
      resolvedDateRange.to ?? new Date(),
    ])
    // .range([
    // 0, 800,
    // new Date(resolvedDateRange.end).getTime() - 1000 * 60 * 60 * 24, // 24 hours back
    // new Date(resolvedDateRange.end).getTime(),
    // ])
  );

  function getDateString(d: Entry) {
    if (d.mills && d.mills > 0) {
      return new Date(d.mills);
    }
    if (d?.dateString) return new Date(d.dateString);
    if (d?.created_at) return new Date(d.created_at);
  }
</script>

<!-- <ChartC.Container config={chartConfig} class="h-fit w-full grid grid-stack"> -->
<!-- <div bind:this={ref} class={cn("w-full", className)} {...restProps}> -->
<LineChart
  data={entries}
  x={(d) => getDateString(d)}
  y={"sgv"}
  c="sgv"
  renderContext={entries.length > 5000 ? "canvas" : "svg"}
  yBaseline={0}
  {xScale}
  xBaseline={resolvedDateRange.to!.getTime()}
  cScale={scaleThreshold()}
  brush
  props={{
    points: {
      r: 3,
    },
  }}
  labels={{
    x: (d) => {
      // try mills first
      if (d.mills && d.mills > 0) {
        return new Date(d.mills).toLocaleTimeString();
      }
      if (d.dateString) {
        return new Date(d.dateString).toLocaleTimeString();
      }

      if (d.created_at) {
        return new Date(d.created_at).toLocaleTimeString();
      }

      return "Unknown";
    },
  }}
  cDomain={[
    thresholds.low,
    thresholds.targetBottom,
    thresholds.targetTop,
    thresholds.high,
  ]}
  cRange={Object.values(chartConfig).map((c) => c.color)}
  tooltip={{}}
  annotations={[
    {
      type: "line",
      y: thresholds.high,
      label: `High (${thresholds.high})`,
      props: {
        label: { class: "text-xs" },
        line: {
          class: "[stroke-dasharray:2,2] stroke-high-bg",
          color: chartConfig.high.color,
        },
      },
    },
    {
      type: "line",
      y: thresholds.low,
      label: `Low (${thresholds.low})`,
      props: {
        label: { class: "text-xs" },
        line: { class: "[stroke-dasharray:2,2] stroke-low-bg" },
      },
    },
  ]}
  padding={{ top: 20, right: 30, bottom: 40, left: 50 }}
></LineChart>
<!-- <Bar x={(d) => getDateString(d)} y={"carbs"} data={scaledTreatments} /> -->
<!-- </div> -->
<!-- </ChartC.Container> -->
