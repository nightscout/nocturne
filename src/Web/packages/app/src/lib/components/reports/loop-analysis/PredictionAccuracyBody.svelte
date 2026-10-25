<!--
  The Prediction accuracy page body: how far each curve the loop predicted with landed from the readings that
  followed, by horizon, by hour of day and by context. Every figure is the API's; the body picks the
  axis, the curve shown in the detail, and lays the panels out.
-->
<script lang="ts">
  import {
    LoopAnalysisSeries,
    PredictionContext,
    PredictionCurve,
    type PredictionAccuracyResponse,
    type PredictionCurveAccuracy,
  } from "$lib/api";
  import * as Alert from "$lib/components/ui/alert";
  import HistoryClampNote from "./HistoryClampNote.svelte";
  import { hoursText } from "./settings-evidence-labels.svelte";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import FigureStrip, { type Figure } from "$lib/components/reports/FigureStrip.svelte";
  import ChartKey, { type ChartKeyItem } from "$lib/components/charts/print/ChartKey.svelte";
  import { aidAlgorithmLabels } from "$lib/components/patient/labels";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { bgDelta, bgLabel, convertToDisplayUnits } from "$lib/utils/formatting";
  import { plural } from "$lib/utils/plural";
  import EyeOff from "@lucide/svelte/icons/eye-off";
  import Info from "@lucide/svelte/icons/info";
  import Lock from "@lucide/svelte/icons/lock";
  import HorizonErrorChart from "./HorizonErrorChart.svelte";
  import PredictionBiasHeatmap from "./PredictionBiasHeatmap.svelte";
  import PredictionContextTable from "./PredictionContextTable.svelte";
  import IgnoredFiltersNotice from "./IgnoredFiltersNotice.svelte";
  import NoTimeZone from "$lib/components/reports/NoTimeZone.svelte";
  import { loopAnalysisTimeZoneNeed } from "./time-zone-need.svelte";
  import { axisEnd } from "./daily-profile-view";
  import { curveLabel, horizonList } from "./prediction-labels.svelte";
  import { loopAnalysisMark } from "./loop-analysis-tour.svelte";

  let { response }: { response: PredictionAccuracyResponse } = $props();

  const primary = $derived(response.primary);
  const comparison = $derived(response.comparison ?? null);
  const curves = $derived(primary?.curves ?? []);
  const comparing = $derived(comparison != null);

  const withheld = $derived(new Set(response.withheldSeries ?? []));
  const curvesHidden = $derived(withheld.has(LoopAnalysisSeries.PredictionCurves));
  const devicesHidden = $derived(withheld.has(LoopAnalysisSeries.PredictionCount));
  const contextsHidden = $derived(withheld.has(LoopAnalysisSeries.PredictionContexts));

  const units = $derived(glucoseUnits.current);
  const unit = $derived(bgLabel());
  const toDisplay = $derived((mgdl: number) => convertToDisplayUnits(mgdl, units));
  const signed = (mgdl: number) => bgDelta(mgdl);
  const unsigned = (mgdl: number) => bgDelta(mgdl, false);

  const CURVE_ORDER = [
    PredictionCurve.Default,
    PredictionCurve.Iob,
    PredictionCurve.ZeroTemp,
    PredictionCurve.Cob,
    PredictionCurve.Uam,
  ];
  const CURVE_COLORS: Record<PredictionCurve, string> = {
    [PredictionCurve.Default]: "var(--pred-main)",
    [PredictionCurve.Iob]: "var(--pred-iob)",
    [PredictionCurve.ZeroTemp]: "var(--pred-zt)",
    [PredictionCurve.Cob]: "var(--pred-cob)",
    [PredictionCurve.Uam]: "var(--pred-uam)",
  };

  const curveOf = (range: typeof primary | null, kind: PredictionCurve) =>
    range?.curves?.find((c) => c.curve === kind) ?? null;

  /** Every curve either range carried, so one only the comparison has still shows. */
  const kinds = $derived(
    CURVE_ORDER.filter((kind) => curveOf(primary, kind) != null || curveOf(comparison, kind) != null)
  );

  /** One symmetric axis for every curve, so their spreads compare at a glance. */
  const yDomain = $derived.by<[number, number]>(() => {
    const ends = [...curves, ...(comparison?.curves ?? [])].flatMap((c) =>
      (c.horizons ?? []).flatMap((h) => [h.error?.p10, h.error?.p90].map((v) => (v == null ? null : Math.abs(toDisplay(v)))))
    );
    const end = axisEnd(ends, units === "mmol" ? 1 : 20);
    return [-end, end];
  });

  let picked = $state<PredictionCurve | null>(null);
  const detail = $derived(curves.find((c) => c.curve === picked) ?? curves[0] ?? null);

  const has = (kind: PredictionCurve) => kinds.includes(kind);
  const horizonsOf = (c: PredictionCurveAccuracy) => c.horizons ?? [];
  const settingsSignal = $derived(
    horizonList((curves[0]?.horizons ?? []).filter((h) => h.settingsSignal).map((h) => h.minutes ?? 0))
  );
  const anySparse = $derived(curves.some((c) => horizonsOf(c).some((h) => h.error?.sparse)));
  const anyInterrupted = $derived(curves.some((c) => horizonsOf(c).some((h) => (h.interrupted ?? 0) > 0)));

  const hiddenCurves = $derived(primary?.hiddenCurves ?? []);

  const key = $derived.by<ChartKeyItem[]>(() => [
    { texture: "percentile-outer", label: "10–90%", color: "color-mix(in oklab, var(--muted-foreground) 28%, transparent)" },
    { texture: "percentile-inner", label: "25–75%", color: "color-mix(in oklab, var(--muted-foreground) 55%, transparent)" },
    { texture: "percentile-median", label: "Bias", color: "var(--foreground)", shape: "line" },
    ...(settingsSignal ? [{ texture: "in-range" as const, label: "Settings signal", color: "var(--muted)" }] : []),
  ]);

  const figures = $derived.by<Figure[]>(() => {
    const list: Figure[] = [
      {
        label: "Predictions analyzed",
        value: (primary?.predictions ?? 0).toLocaleString(),
        note: comparing
          ? `${(comparison?.predictions ?? 0).toLocaleString()} in the compared range`
          : "One per device per five minutes",
      },
    ];
    if ((primary?.algorithms?.length ?? 0) > 0)
      list.push({ label: "From", value: (primary?.algorithms ?? []).map((a) => aidAlgorithmLabels[a]).join(", ") });
    return list;
  });
</script>

{#if response.timeZoneUnavailableReason}
  <NoTimeZone reason={response.timeZoneUnavailableReason}>{loopAnalysisTimeZoneNeed()}</NoTimeZone>
{:else}
  <div class="space-y-6">
    <HistoryClampNote {response}>
      The report adds the most recent predictions when their readings arrive.
    </HistoryClampNote>

    <IgnoredFiltersNotice ignored={response.ignoredFilters} />

    {#if curvesHidden}
      <div class="flex flex-col items-center gap-3 rounded-lg border border-dashed px-6 py-10 text-center">
        <Lock class="size-10 text-muted-foreground/50" />
        <h2 class="font-medium">Prediction accuracy is hidden</h2>
        <p class="max-w-prose text-sm text-muted-foreground">
          {#if devicesHidden}
            The predictions come from the device status of your loop. Your access does not include
            device data.
          {:else}
            The report compares the predictions with your glucose readings. Your access does not
            include glucose.
          {/if}
          A person who manages this account can give you access.
        </p>
      </div>
    {:else}
      <FigureStrip {figures} />

      {#if hiddenCurves.length > 0}
        <p class="flex items-start gap-1.5 px-1 text-sm text-muted-foreground">
          <EyeOff class="mt-0.5 size-4 shrink-0" />
          {plural(hiddenCurves.length, ["This curve is hidden", "These curves are hidden"])}:
          {hiddenCurves.map(curveLabel).join(", ")}. The report reads each curve only in the contexts
          where the algorithm uses it. These contexts come from treatments, and your access does not
          include treatments.
        </p>
      {/if}

      {#if kinds.length === 0}
        <div class="rounded-lg border border-dashed p-8 text-center text-muted-foreground">
          <p class="font-medium">No predictions in this range</p>
          <p class="text-sm">
            Prediction accuracy needs the predictions that your loop uploads with its device status.
            {#if response.historyClamped}
              {plural(response.historyWindowHours ?? 0, [
                "You can see only the last hour, and it has no predictions yet.",
                "You can see only the last # hours, and they have no predictions yet.",
              ])}
            {:else}
              Select a longer range, or use fewer filters.
            {/if}
          </p>
        </div>
      {:else}
        <Alert.Root variant="info">
          <Info />
          <Alert.Title>How to read these figures</Alert.Title>
          <Alert.Description>
            <ul class="list-disc space-y-1 pl-4">
              <li>
                Bias is the prediction minus the reading that followed it. A positive bias means that
                the prediction was too high. MAE (mean absolute error) is the average size of the
                error, in either direction.
              </li>
              {#if settingsSignal}
                <li>
                  Only the bias at {settingsSignal} gives information about your settings. The
                  readings at longer horizons include all the actions of the loop after the
                  prediction. The prediction assumed that these actions would not occur.
                </li>
              {/if}
              <li>
                A prediction is interrupted if a carb entry or manual bolus occurs before its
                horizon. No bias figure includes interrupted predictions, because the reading then
                measures that action.
              </li>
              {#if has(PredictionCurve.Uam)}
                <li>
                  oref gives its unannounced meal (UAM) curve only when glucose increases faster than
                  insulin alone predicts, or when carbs are absorbing. So the report measures UAM
                  accuracy only at these times.
                </li>
              {/if}
              {#if has(PredictionCurve.ZeroTemp)}
                <li>
                  oref removes the end of its zero-temp curve where the curve increases above the
                  target. So its long horizons come only from predictions that did not increase
                  above the target.
                </li>
              {/if}
            </ul>
          </Alert.Description>
        </Alert.Root>

        <section
          class="space-y-3 rounded-lg border bg-card p-3 @md:p-4"
          aria-labelledby="prediction-error-title"
          data-testid="loop-analysis-prediction-error"
          {@attach loopAnalysisMark("loop-analysis-prediction-accuracy.horizons")}
        >
          <div class="space-y-1 px-1">
            <h2 id="prediction-error-title" class="font-medium">Error by horizon</h2>
            <p class="text-xs text-muted-foreground">
              The spread of prediction minus reading, in {unit}, at each horizon. The bold line is the
              bias. The MAE and the number of predictions are above each horizon.
            </p>
          </div>
          <div class="flex flex-wrap items-center gap-x-4 gap-y-1 px-1">
            <ChartKey class="justify-start" items={key} />
            {#if comparing}
              <span class="flex items-center gap-1.5 text-xs text-muted-foreground">
                <svg viewBox="0 0 44 12" class="inline-block h-3 w-11 shrink-0" aria-hidden="true">
                  <line x1="0" y1="6" x2="44" y2="6" class="stroke-foreground" stroke-width="1.5" stroke-dasharray="3 2" />
                </svg>
                Compared range
              </span>
            {/if}
          </div>
          <div class="grid gap-x-6 gap-y-8 @2xl:grid-cols-2">
            {#each kinds as kind (kind)}
              <HorizonErrorChart
                {kind}
                curve={curveOf(primary, kind)}
                comparison={curveOf(comparison, kind)}
                color={CURVE_COLORS[kind]}
                {yDomain}
                scale={toDisplay}
                formatError={signed}
                formatMae={unsigned}
                {unit}
              />
            {/each}
          </div>
          {#if anySparse}
            <p class="flex items-center gap-1.5 px-1 text-xs text-muted-foreground">
              <EyeOff class="size-3.5" />
              Faded horizons have too few days to show a pattern.
            </p>
          {/if}
          <p class="px-1 text-xs text-muted-foreground">
            Use the arrow keys to move between horizons. Use Home and End to go to the first or the
            last horizon.
          </p>
        </section>

        {#if detail}
          <section class="space-y-4 rounded-lg border bg-card p-3 @md:p-4" aria-labelledby="prediction-detail-title" data-testid="loop-analysis-prediction-detail">
            <div class="flex flex-wrap items-center justify-between gap-2 px-1">
              <h2 id="prediction-detail-title" class="font-medium">By hour and context</h2>
              {#if curves.length > 1}
                <ToggleGroup.Root
                  type="single"
                  variant="segmented"
                  size="xs"
                  aria-label="Curve"
                  value={detail.curve ?? ""}
                  onValueChange={(next: string) => {
                    const curve = curves.find((c) => c.curve === next)?.curve;
                    if (curve) picked = curve;
                  }}
                >
                  {#each curves as curve (curve.curve)}
                    <ToggleGroup.Item value={curve.curve ?? ""}>{curveLabel(curve.curve)}</ToggleGroup.Item>
                  {/each}
                </ToggleGroup.Root>
              {/if}
            </div>

            <div class="space-y-2" {@attach loopAnalysisMark("loop-analysis-prediction-accuracy.hours")}>
              <h3 class="px-1 text-sm font-medium">{curveLabel(detail.curve)} bias by hour of day</h3>
              <p class="px-1 text-xs text-muted-foreground">
                By the local hour of the prediction, in {unit}.
                {#if comparing}The compared range is in the charts above.{/if}
              </p>
              <PredictionBiasHeatmap curve={detail} formatError={signed} {unit} />
            </div>

            <div class="space-y-2" {@attach contextsHidden ? undefined : loopAnalysisMark("loop-analysis-prediction-accuracy.contexts")}>
              <h3 class="px-1 text-sm font-medium">{curveLabel(detail.curve)} by context</h3>
              {#if contextsHidden}
                <p class="px-1 text-sm text-muted-foreground">
                  Hidden. The contexts and the interrupted counts come from carbs, boluses, overrides
                  and exercise. Your access does not include treatments.
                </p>
              {:else}
                <p class="px-1 text-xs text-muted-foreground">
                  The context of a prediction is what occurred in the {hoursText(response.contextLookbackMs)} before it.
                  {#if (detail.contexts ?? []).includes(PredictionContext.Adjusted)}
                    Override, temporary target or exercise includes low "eating soon" temporary targets.
                    So some predictions made just before a meal are in that context, not in After a
                    meal.
                  {/if}
                  {#if anyInterrupted}
                    The bias, MAE and n do not include interrupted predictions.
                  {/if}
                </p>
                <PredictionContextTable
                  curve={detail}
                  comparison={curveOf(comparison, detail.curve ?? PredictionCurve.Default)}
                  formatError={signed}
                  formatMae={unsigned}
                  {unit}
                  lookbackMs={response.contextLookbackMs}
                />
              {/if}
            </div>
          </section>
        {/if}

        <p class="px-1 text-xs text-muted-foreground">
          {plural(kinds.length, ["# curve", "# curves"])} from the predictions that your loop uploaded.
          The report pairs each prediction with the reading within 2.5 minutes of its horizon.
          Unmatched horizons had no reading. Excluded horizons were in a sensor error or a
          compression low.
        </p>
      {/if}
    {/if}
  </div>
{/if}
