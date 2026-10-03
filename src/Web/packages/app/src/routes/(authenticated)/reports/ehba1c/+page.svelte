<script lang="ts">
  import { onMount } from "svelte";
  import { SvelteMap } from "svelte/reactivity";
  import { LineChart, Tooltip } from "layerchart";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Activity from "@lucide/svelte/icons/activity";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import {
    getAvailableYears,
    getEHbA1cTimeline,
  } from "$api/generated/dataOverviews.generated.remote";
  import * as labResultsApi from "$api/generated/labHbA1cs.generated.remote";
  import type {
    EHbA1cPoint,
    LabHbA1cResult,
    A1cDisplayValue,
    A1cDisplayReferences,
  } from "$api/generated/nocturne-api-client";
  import { a1cUnits } from "$lib/stores/appearance-store.svelte";
  import {
    a1cLabel,
    a1cUnitLabel,
    a1cDisplayValue,
    formatA1c,
    formatA1cNumber,
  } from "$lib/utils/a1c-formatting";
  import { bg, bgLabel, formatLongDate } from "$lib/utils/formatting";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { setReportPrintMeta } from "$lib/components/reports/print/report-print.svelte";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import {
    CHART_TEXTURES,
    patternClass,
    type TextureKey,
  } from "$lib/components/charts/print/chart-print-patterns";

  type ChartPoint = {
    date: Date;
    estimatedA1cPercent: number;
    a1cDisplay?: A1cDisplayValue;
    weightedAverageGlucoseMgdl: number;
    readingCount: number;
    daysWithData: number;
  };

  // Display bounds come from the same backend conversion as the plotted values.
  const A1C_ZONES: {
    key: string;
    label: string;
    swatch: string;
    texture: Extract<TextureKey, `ehba1c-zone-${string}`>;
  }[] = [
    {
      key: "healthy",
      label: "Non-diabetic range",
      swatch: "var(--gri-zone-a)",
      texture: "ehba1c-zone-healthy",
    },
    {
      key: "target",
      label: "Type 1 diabetes target",
      swatch: "var(--gri-zone-b)",
      texture: "ehba1c-zone-target",
    },
    {
      key: "high",
      label: "Elevated",
      swatch: "var(--gri-zone-d)",
      texture: "ehba1c-zone-high",
    },
    {
      key: "veryHigh",
      label: "Very high",
      swatch: "var(--gri-zone-e)",
      texture: "ehba1c-zone-very-high",
    },
  ];

  let loading = $state(true);
  let error = $state<unknown>(null);
  let pointsByYear = $state<Map<number, EHbA1cPoint[]>>(new Map());
  let references = $state<A1cDisplayReferences>();

  let labResults = $state<LabHbA1cResult[]>([]);
  let newLabDate = $state("");
  let newLabValue = $state("");
  let newLabNote = $state("");

  // A draft entered in the previous unit must not be submitted in the new unit.
  $effect(() => {
    void a1cUnits.current;
    newLabValue = "";
  });
  let savingLabResult = $state(false);
  let labResultError = $state<string | null>(null);
  let pendingDeleteLabResult = $state<LabHbA1cResult | null>(null);

  /**
   * Normalizes a lab result's measuredAt (Date instance or ISO string) to a
   * Date.
   */
  function toDate(value: Date | string | undefined): Date {
    return value instanceof Date ? value : new Date(value ?? 0);
  }

  function dateKey(date: Date): string {
    return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
  }

  function toChartPoints(pointsMap: Map<number, EHbA1cPoint[]>): ChartPoint[] {
    const all: ChartPoint[] = [];
    for (const points of pointsMap.values()) {
      for (const point of points) {
        const [y, m, d] = (point.date ?? "").split("-").map(Number);
        if (!y || !m || !d) continue;
        all.push({
          date: new Date(y, m - 1, d),
          estimatedA1cPercent: point.estimatedA1cPercent ?? 0,
          a1cDisplay: point.a1cDisplay,
          weightedAverageGlucoseMgdl: point.weightedAverageGlucoseMgdl ?? 0,
          readingCount: point.readingCount ?? 0,
          daysWithData: point.daysWithData ?? 0,
        });
      }
    }
    return all.sort((a, b) => a.date.getTime() - b.date.getTime());
  }

  const chartData = $derived(toChartPoints(pointsByYear));

  const timelineBounds = $derived.by(() => {
    const dates = [...pointsByYear.values()]
      .flat()
      .map((p) => p.date)
      .filter((d): d is string => !!d)
      .sort();
    return dates.length > 0
      ? { from: dates[0], to: dates[dates.length - 1] }
      : null;
  });

  setReportPrintMeta(() => (timelineBounds ? { period: timelineBounds } : {}));
  const latest = $derived(
    chartData.length > 0 ? chartData[chartData.length - 1] : undefined
  );

  const extremes = $derived.by(() => {
    if (chartData.length === 0) return undefined;
    let highest = chartData[0];
    let lowest = chartData[0];
    for (const p of chartData) {
      if (p.estimatedA1cPercent > highest.estimatedA1cPercent) highest = p;
      if (p.estimatedA1cPercent < lowest.estimatedA1cPercent) lowest = p;
    }
    return { highest, lowest };
  });

  const zoneBands = $derived(
    A1C_ZONES.map((zone, i) => {
      return {
        ...zone,
        isLast: i === A1C_ZONES.length - 1,
        yMin:
          a1cDisplayValue(
            i === 0
              ? references?.minimum
              : [references?.healthy, references?.target, references?.elevated][
                  i - 1
                ]
          ) ?? 0,
        yMax:
          a1cDisplayValue(
            [
              references?.healthy,
              references?.target,
              references?.elevated,
              references?.veryHigh,
            ][i]
          ) ?? 0,
      };
    })
  );

  function formatZoneRange(band: (typeof zoneBands)[number]): string {
    const unit = `${a1cUnits.current === "percent" ? "" : " "}${a1cUnitLabel()}`;
    if (band.isLast) return `> ${formatA1cNumber(band.yMin)}${unit}`;
    return `${formatA1cNumber(band.yMin)}–${formatA1cNumber(band.yMax)}${unit}`;
  }

  /**
   * Fixed floor at the never-goes-lower bound; auto-scaled ceiling with a
   * little headroom.
   */
  const yDomain = $derived.by((): [number, number] => {
    const dataMax =
      chartData.length > 0
        ? Math.max(...chartData.map((p) => a1cDisplayValue(p.a1cDisplay) ?? 0))
        : (a1cDisplayValue(references?.target) ?? 0);
    const padding =
      a1cUnits.current === "percent" ? 0.5 : (references?.mmolMolPadding ?? 0);
    return [
      a1cDisplayValue(references?.minimum) ?? 0,
      Math.max(dataMax + padding, a1cDisplayValue(references?.target) ?? 0),
    ];
  });

  // Clamped to yDomain so a band never computes to a pixel position outside the plot area —
  // an unclamped "very high" band (up to 14%) otherwise rendered above the chart's actual
  // top edge and, since the chart container doesn't clip by default, bled out over the toggle.
  const annotations = $derived(
    zoneBands
      .map((band) => {
        const yMin = Math.max(band.yMin, yDomain[0]);
        const yMax = Math.min(band.yMax, yDomain[1]);
        if (yMax <= yMin) return null;
        const y: [number, number] = [yMin, yMax];
        return {
          type: "range" as const,
          layer: "below" as const,
          y,
          fill: CHART_TEXTURES[band.texture].color,
          class: patternClass(band.texture),
        };
      })
      .filter((band) => band !== null)
  );

  async function loadAll() {
    loading = true;
    error = null;
    try {
      const [{ years }, results] = await Promise.all([
        getAvailableYears().run(),
        labResultsApi.getAll().run(),
      ]);
      labResults = results ?? [];
      const pointResults = await Promise.all(
        (years?.length ? years : [new Date().getFullYear()]).map(
          async (year) => {
            const response = await getEHbA1cTimeline({ year }).run();
            references = response.a1cReferences;
            return [year, response.points ?? []] as const;
          }
        )
      );
      pointsByYear = new Map(pointResults);
    } catch (err) {
      error = err;
      console.error("Failed to load eHbA1c timeline:", err);
    } finally {
      loading = false;
    }
  }

  /**
   * Lab draws are shown as standalone markers — never connected by a line and
   * never fed back into the eHbA1c calculation, which only ever reads
   * SensorGlucose/MeterGlucose.
   */
  const labChartPoints = $derived(
    labResults.map((r) => ({
      id: r.id,
      date: toDate(r.measuredAt),
      valuePercent: r.valuePercent ?? 0,
      a1cDisplay: r.a1cDisplay,
      displayValue: a1cDisplayValue(r.a1cDisplay) ?? 0,
      note: r.note,
    }))
  );

  function displayValueForChartDate(date: Date): number | null {
    if (chartData.length === 0) return null;
    const timestamp = date.getTime();
    if (
      timestamp < chartData[0].date.getTime() ||
      timestamp > chartData[chartData.length - 1].date.getTime()
    ) {
      return null;
    }
    const nextIndex = chartData.findIndex(
      (point) => point.date.getTime() >= timestamp
    );
    if (nextIndex === 0)
      return a1cDisplayValue(chartData[0].a1cDisplay) ?? null;
    if (nextIndex === -1)
      return (
        a1cDisplayValue(chartData[chartData.length - 1].a1cDisplay) ?? null
      );

    const previous = chartData[nextIndex - 1];
    const next = chartData[nextIndex];
    const progress =
      (timestamp - previous.date.getTime()) /
      (next.date.getTime() - previous.date.getTime());
    const previousValue = a1cDisplayValue(previous.a1cDisplay) ?? 0;
    const nextValue = a1cDisplayValue(next.a1cDisplay) ?? 0;
    return previousValue + (nextValue - previousValue) * progress;
  }

  const displayChartData = $derived.by(() => {
    const rows = new SvelteMap<number, { date: Date; displayValue: number }>();
    for (const point of chartData) {
      rows.set(point.date.getTime(), {
        date: point.date,
        displayValue: a1cDisplayValue(point.a1cDisplay) ?? 0,
      });
    }
    // A lab-only date gets a synthetic row with the interpolated estimate solely to give the tooltip
    // a hover target there. `extremes` and `yDomain` read `chartData`, so the summaries never see
    // these rows, and lab draws stay out of the calculation (see `labChartPoints`). Declaring lab
    // draws as a second series instead would be wrong: LineChart renders a Spline per visible
    // series and would join the lab points with a line.
    for (const labPoint of labChartPoints) {
      if (!rows.has(labPoint.date.getTime())) {
        const displayValue = displayValueForChartDate(labPoint.date);
        if (displayValue !== null) {
          rows.set(labPoint.date.getTime(), {
            date: labPoint.date,
            displayValue,
          });
        }
      }
    }
    return [...rows.values()].sort(
      (a, b) => a.date.getTime() - b.date.getTime()
    );
  });

  async function addLabResult() {
    const value = Number(newLabValue);
    if (!newLabDate || !newLabValue || Number.isNaN(value)) return;
    savingLabResult = true;
    labResultError = null;
    try {
      await labResultsApi.create({
        measuredAt: new Date(`${newLabDate}T00:00:00.000Z`).toISOString(),
        value,
        unit: a1cUnits.current,
        note: newLabNote || undefined,
      });
      labResults = (await labResultsApi.getAll().run()) ?? [];
      newLabDate = "";
      newLabValue = "";
      newLabNote = "";
    } catch (err) {
      labResultError = describeSubmitError(err, "Failed to save lab result");
    } finally {
      savingLabResult = false;
    }
  }

  async function confirmDeleteLabResult() {
    const target = pendingDeleteLabResult;
    pendingDeleteLabResult = null;
    if (!target?.id) return;
    try {
      await labResultsApi.remove(target.id);
      labResults = (await labResultsApi.getAll().run()) ?? [];
    } catch (err) {
      labResultError = describeSubmitError(err, "Failed to delete lab result");
    }
  }

  onMount(() => {
    queueMicrotask(loadAll);
  });
</script>

<div class="@container space-y-6">
  <Card.Root>
    <Card.Header
      class="flex flex-row flex-wrap items-start justify-between gap-4"
    >
      <div>
        <Card.Title class="flex items-center gap-2">
          <Activity class="h-5 w-5 text-muted-foreground" />
          Estimated {a1cLabel()} ({a1cLabel(true)})
        </Card.Title>
        <Card.Description>
          {a1cLabel(true)} estimates what a lab {a1cLabel()} test would read on a
          given day. For each day, it takes the average glucose from every reading
          in the trailing 90 days and weights each day's contribution by recency —
          the most recent ~30 days count for roughly half the estimate, tapering off
          exponentially for older days, similar to how glycated hemoglobin reflects
          glucose exposure over a red blood cell's ~90–120 day lifespan. That weighted
          average glucose is then converted to NGSP percent with the ADAG formula:
          {a1cLabel(true)} (%) = (average glucose in mg/dL + 46.7) / 28.7.
        </Card.Description>
      </div>
      <p class="hidden shrink-0 text-sm text-muted-foreground print:block">
        {a1cUnits.current === "percent"
          ? "Shown in % (NGSP)"
          : "Shown in mmol/mol (IFCC)"}
      </p>
    </Card.Header>
    <Card.Content>
      {#if loading}
        <div
          class="flex h-[320px] items-center justify-center text-muted-foreground"
        >
          <Loader2 class="h-5 w-5 animate-spin mr-2" /> Loading {a1cLabel(true)} timeline...
        </div>
      {:else if error}
        <div
          class="flex h-[320px] items-center justify-center text-destructive"
        >
          Failed to load {a1cLabel(true)} data. Please try again later.
        </div>
      {:else if chartData.length === 0}
        <div
          class="flex h-[320px] items-center justify-center text-muted-foreground"
        >
          Not enough glucose history yet — each point needs at least a month of
          readings within the trailing 90 days.
        </div>
      {:else}
        {#if latest && extremes}
          <div class="mb-4 flex flex-wrap items-baseline gap-x-6 gap-y-1">
            <div>
              <span class="text-3xl font-semibold tabular-nums">
                {formatA1c(latest.a1cDisplay)}
              </span>
              <span class="ml-2 text-sm text-muted-foreground">
                latest estimate ({formatLongDate(latest.date)})
              </span>
            </div>
            <div class="text-sm text-muted-foreground">
              Weighted mean glucose: {bg(latest.weightedAverageGlucoseMgdl)}
              {bgLabel()}
            </div>
            <div class="text-sm text-muted-foreground">
              Highest: <span class="font-medium text-foreground">
                {formatA1c(extremes.highest.a1cDisplay)}
              </span>
              ({formatLongDate(extremes.highest.date)})
            </div>
            <div class="text-sm text-muted-foreground">
              Lowest: <span class="font-medium text-foreground">
                {formatA1c(extremes.lowest.a1cDisplay)}
              </span>
              ({formatLongDate(extremes.lowest.date)})
            </div>
          </div>
        {/if}

        <div class="h-[320px] w-full @md:h-[400px]" data-testid="ehba1c-chart">
          <LineChart
            data={displayChartData}
            x="date"
            y="displayValue"
            {yDomain}
            clip
            series={[
              {
                key: "displayValue",
                label: `${a1cLabel(true)} ${a1cUnitLabel()}`,
                color: "var(--ehba1c-line)",
              },
            ]}
            props={{
              spline: {
                "stroke-width": 3,
                "stroke-linecap": "round",
                "data-testid": "ehba1c-line",
              },
            }}
            points={{
              data: labChartPoints,
              x: (d) => d.date,
              y: (d) => d.displayValue,
              children: labMarkers,
            }}
            {annotations}
          >
            {#snippet tooltip({ context })}
              <Tooltip.Root
                {context}
                class="bg-popover text-popover-foreground rounded-md border p-3 shadow-lg"
              >
                {#snippet children({ data })}
                  {@const hoveredDate = context.x(data)}
                  {@const hoveredDateKey = dateKey(hoveredDate)}
                  {@const eHbA1cPoint = chartData.find(
                    (point) => dateKey(point.date) === hoveredDateKey
                  )}
                  {@const labResultsForDate = labChartPoints.filter(
                    (point) => dateKey(point.date) === hoveredDateKey
                  )}
                  <div class="mb-2 text-sm font-semibold">
                    {formatLongDate(hoveredDate)}
                  </div>
                  <div
                    class="min-w-56 space-y-1.5 text-sm"
                    data-testid="ehba1c-tooltip"
                  >
                    {#if eHbA1cPoint}
                      <div
                        class="grid grid-cols-[1fr_auto] items-center gap-x-4"
                      >
                        <span
                          class="flex min-w-0 items-center gap-2 text-muted-foreground"
                        >
                          <span
                            class="h-2 w-2 shrink-0 rounded-full bg-(--ehba1c-line)"
                          ></span>
                          <span>{a1cLabel(true)}</span>
                        </span>
                        <span class="font-mono font-medium tabular-nums">
                          {formatA1c(eHbA1cPoint.a1cDisplay)}
                        </span>
                      </div>
                    {/if}
                    {#each labResultsForDate as labResult (labResult.id)}
                      <div
                        class="grid grid-cols-[1fr_auto] items-center gap-x-4"
                      >
                        <span
                          class="flex min-w-0 items-center gap-2 text-muted-foreground"
                        >
                          <span
                            class="h-0 w-0 shrink-0 border-x-4 border-b-8 border-x-transparent border-b-foreground"
                          ></span>
                          <span>Lab result</span>
                        </span>
                        <span class="font-mono font-medium tabular-nums">
                          {formatA1c(labResult.a1cDisplay)}
                        </span>
                        {#if labResult.note}
                          <span
                            class="col-span-2 truncate text-xs text-muted-foreground"
                          >
                            {labResult.note}
                          </span>
                        {/if}
                      </div>
                    {/each}
                  </div>
                {/snippet}
              </Tooltip.Root>
            {/snippet}
          </LineChart>
        </div>

        <div
          class="mt-4 flex flex-wrap items-center justify-center gap-x-5 gap-y-2 text-sm"
        >
          <ChartKey
            class="text-sm text-foreground"
            items={zoneBands.map((band) => ({
              texture: band.texture,
              color: band.swatch,
              label: `${band.label} (${formatZoneRange(band)})`,
            }))}
          />
          {#if labChartPoints.length > 0}
            <div class="flex items-center gap-1.5">
              <span
                class="inline-block h-0 w-0 border-x-4 border-b-7 border-x-transparent border-b-foreground"
              ></span>
              <span>Lab result (not included in the calculation)</span>
            </div>
          {/if}
        </div>
      {/if}
    </Card.Content>
  </Card.Root>

  <Card.Root class={labResults.length === 0 ? "print:hidden" : undefined}>
    <Card.Header>
      <Card.Title>Lab results</Card.Title>
      <p class="hidden text-sm text-muted-foreground print:block">
        Lab {a1cLabel()} draws, shown as triangles on the chart above. They are not
        included in the
        {a1cLabel(true)} calculation.
      </p>
      <Card.Description class="print:hidden">
        Enter a lab {a1cLabel()} result here — shown as a triangle marker on the chart
        above, so you can see how closely the {a1cLabel(true)} estimate tracks an
        actual lab draw. Lab results are not included in the {a1cLabel(true)} calculation
        itself. The date below is the date the blood was drawn, not the date you enter
        it here.
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-4">
      {#if labResults.length === 0}
        <p class="text-sm text-muted-foreground">No lab results added yet.</p>
      {:else}
        <ul class="divide-border divide-y">
          {#each [...labResults].sort((a, b) => toDate(b.measuredAt).getTime() - toDate(a.measuredAt).getTime()) as result (result.id)}
            <li class="flex items-center justify-between gap-3 py-2">
              <div>
                <div class="text-sm font-medium">
                  {formatA1c(result.a1cDisplay)}
                  {#if result.note}<span
                      class="text-muted-foreground font-normal"
                    >
                      — {result.note}
                    </span>{/if}
                </div>
                <div class="text-xs text-muted-foreground">
                  {formatLongDate(toDate(result.measuredAt))}
                </div>
              </div>
              <Button
                variant="ghost"
                size="icon"
                class="print:hidden"
                aria-label="Delete lab result"
                onclick={() => (pendingDeleteLabResult = result)}
              >
                <Trash2 class="size-4" />
              </Button>
            </li>
          {/each}
        </ul>
      {/if}

      <div class="grid gap-3 sm:grid-cols-3 print:hidden">
        <div class="space-y-1.5">
          <Label for="lab-date">Date of blood draw</Label>
          <Input id="lab-date" type="date" bind:value={newLabDate} />
        </div>
        <div class="space-y-1.5">
          <Label for="lab-value">
            Result ({a1cUnits.current === "percent" ? "%" : "mmol/mol"})
          </Label>
          <Input
            id="lab-value"
            type="number"
            step={a1cUnits.current === "percent" ? "0.1" : "1"}
            bind:value={newLabValue}
            placeholder={a1cUnits.current === "percent"
              ? "e.g. 7.0"
              : "e.g. 53"}
          />
        </div>
        <div class="space-y-1.5">
          <Label for="lab-note">Note (optional)</Label>
          <Input
            id="lab-note"
            type="text"
            bind:value={newLabNote}
            placeholder="e.g. GP lab"
          />
        </div>
      </div>

      {#if labResultError}
        <p class="text-destructive text-sm print:hidden">{labResultError}</p>
      {/if}

      <Button
        class="print:hidden"
        onclick={addLabResult}
        disabled={savingLabResult || !newLabDate || !newLabValue}
      >
        {#if savingLabResult}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Plus class="size-4" />
        {/if}
        Add lab result
      </Button>
    </Card.Content>
  </Card.Root>
</div>

{#snippet labMarkers({
  points,
}: {
  points: { x: number; y: number; data: (typeof labChartPoints)[number] }[];
})}
  {#each points as point (point.data.id)}
    <polygon
      data-testid="lab-marker"
      points="{point.x},{point.y - 7} {point.x - 6},{point.y + 5} {point.x +
        6},{point.y + 5}"
      class="fill-foreground stroke-background"
      stroke-width="1"
      pointer-events="none"
    >
      <title>
        Lab result: {formatA1c(point.data.a1cDisplay)} ({formatLongDate(
          point.data.date
        )}){point.data.note ? ` — ${point.data.note}` : ""}
      </title>
    </polygon>
  {/each}
{/snippet}

<ConfirmDialog
  open={pendingDeleteLabResult !== null}
  onOpenChange={(o) => {
    if (!o) pendingDeleteLabResult = null;
  }}
  title="Delete this lab result?"
  confirmLabel="Delete"
  onConfirm={confirmDeleteLabResult}
>
  {#snippet description()}
    {#if pendingDeleteLabResult}
      Delete the {formatA1c(pendingDeleteLabResult.a1cDisplay)} lab result from
      {formatLongDate(toDate(pendingDeleteLabResult.measuredAt))}.
    {/if}
  {/snippet}
</ConfirmDialog>
