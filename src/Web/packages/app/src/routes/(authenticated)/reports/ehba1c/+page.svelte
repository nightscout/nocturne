<script lang="ts">
  import { onMount } from "svelte";
  import { LineChart } from "layerchart";
  import { Loader2, Activity, Plus, Trash2 } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import {
    getAvailableYears,
    getEHbA1cTimeline,
  } from "$api/generated/dataOverviews.generated.remote";
  import * as labResultsApi from "$api/generated/labHbA1cs.generated.remote";
  import type { EHbA1cPoint, LabHbA1cResult } from "$api/generated/nocturne-api-client";
  import { bg, bgLabel, formatLongDate } from "$lib/utils/formatting";
  import { describeSubmitError } from "$lib/forms/submit-error";

  type ChartPoint = {
    date: Date;
    estimatedA1cPercent: number;
    weightedAverageGlucoseMgdl: number;
    readingCount: number;
    daysWithData: number;
  };

  type A1cUnit = "percent" | "mmol";

  /**
   * No lab reference range for a non-diabetic adult goes below this, so the chart neither
   * draws nor colors that region — it would only be empty space.
   */
  const MIN_A1C_PERCENT = 4.0;

  /**
   * Reference zones for the legend/bands, in DCCT/NGSP %. Bounds are the widely-cited ADA
   * thresholds (normal < 5.7%, prediabetes 5.7–6.4%, diabetes ≥ 6.5%); "Type 1 diabetes
   * target" uses the general ADA adult target of < 7.0%, and "Elevated"/"Very high" split the
   * range above that at 9.0%, where complication risk rises sharply. Swatches reuse the GRI
   * report's green/yellow-green/orange/red severity scale; band fills use dedicated,
   * dark-mode-tuned tokens (the swatch colors are too subtle at chart-fill opacity, and
   * --glucose-in-range/--chart-2 turned out to be the same color when tried here).
   */
  const A1C_ZONES: { key: string; label: string; maxPercent: number; swatch: string; fill: string }[] = [
    { key: "healthy", label: "Non-diabetic range", maxPercent: 5.7, swatch: "var(--gri-zone-a)", fill: "var(--ehba1c-zone-healthy)" },
    { key: "target", label: "Type 1 diabetes target", maxPercent: 7.0, swatch: "var(--gri-zone-b)", fill: "var(--ehba1c-zone-target)" },
    { key: "high", label: "Elevated", maxPercent: 9.0, swatch: "var(--gri-zone-d)", fill: "var(--ehba1c-zone-high)" },
    { key: "veryHigh", label: "Very high", maxPercent: 14.0, swatch: "var(--gri-zone-e)", fill: "var(--ehba1c-zone-very-high)" },
  ];

  let loading = $state(true);
  let error = $state<unknown>(null);
  let pointsByYear = $state<Map<number, EHbA1cPoint[]>>(new Map());
  let a1cUnit = $state<A1cUnit>("percent");

  let labResults = $state<LabHbA1cResult[]>([]);
  let newLabDate = $state("");
  let newLabValue = $state("");
  let newLabNote = $state("");
  let savingLabResult = $state(false);
  let labResultError = $state<string | null>(null);
  let pendingDeleteLabResult = $state<LabHbA1cResult | null>(null);

  /** NGSP % to IFCC mmol/mol, the standard dual-reporting conversion for HbA1c. */
  function toIfccMmolMol(percent: number): number {
    return (percent - 2.15) * 10.929;
  }

  function toDisplayUnit(percent: number): number {
    return a1cUnit === "percent" ? percent : toIfccMmolMol(percent);
  }

  /** Normalizes a lab result's measuredAt (Date instance or ISO string) to a Date. */
  function toDate(value: Date | string | undefined): Date {
    return value instanceof Date ? value : new Date(value ?? 0);
  }

  /** IFCC mmol/mol to NGSP %, the inverse of toIfccMmolMol — used to store a mmol/mol entry as %. */
  function toPercentFromDisplayUnit(value: number): number {
    return a1cUnit === "percent" ? value : value / 10.929 + 2.15;
  }

  function formatA1c(percent: number): string {
    return a1cUnit === "percent"
      ? `${percent.toFixed(1)}%`
      : `${Math.round(toIfccMmolMol(percent))} mmol/mol`;
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
          weightedAverageGlucoseMgdl: point.weightedAverageGlucoseMgdl ?? 0,
          readingCount: point.readingCount ?? 0,
          daysWithData: point.daysWithData ?? 0,
        });
      }
    }
    return all.sort((a, b) => a.date.getTime() - b.date.getTime());
  }

  const chartData = $derived(toChartPoints(pointsByYear));

  const displayChartData = $derived(
    chartData.map((p) => ({ ...p, displayValue: toDisplayUnit(p.estimatedA1cPercent) }))
  );

  const latest = $derived(chartData.length > 0 ? chartData[chartData.length - 1] : undefined);

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
      const minPercent = i === 0 ? MIN_A1C_PERCENT : A1C_ZONES[i - 1].maxPercent;
      return {
        ...zone,
        minPercent,
        isLast: i === A1C_ZONES.length - 1,
        yMin: toDisplayUnit(minPercent),
        yMax: toDisplayUnit(zone.maxPercent),
      };
    })
  );

  function formatZoneRange(band: (typeof zoneBands)[number]): string {
    const unit = a1cUnit === "percent" ? "%" : " mmol/mol";
    const fmt = (p: number) => (a1cUnit === "percent" ? p.toFixed(1) : `${Math.round(toIfccMmolMol(p))}`);
    if (band.isLast) return `> ${fmt(band.minPercent)}${unit}`;
    return `${fmt(band.minPercent)}–${fmt(band.maxPercent)}${unit}`;
  }

  /** Fixed floor at the never-goes-lower bound; auto-scaled ceiling with a little headroom. */
  const yDomain = $derived.by((): [number, number] => {
    const dataMaxPercent =
      chartData.length > 0
        ? Math.max(...chartData.map((p) => p.estimatedA1cPercent))
        : A1C_ZONES[1].maxPercent;
    const maxPercent = Math.max(dataMaxPercent + 0.5, A1C_ZONES[1].maxPercent);
    return [toDisplayUnit(MIN_A1C_PERCENT), toDisplayUnit(maxPercent)];
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
        return {
          type: "range" as const,
          layer: "below" as const,
          y: [yMin, yMax] as [number, number],
          fill: band.fill,
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
        (years ?? []).map(async (year) => {
          const response = await getEHbA1cTimeline({ year }).run();
          return [year, response.points ?? []] as const;
        })
      );
      pointsByYear = new Map(pointResults);
    } catch (err) {
      error = err;
      console.error("Failed to load eHbA1c timeline:", err);
    } finally {
      loading = false;
    }
  }

  /** Lab draws are shown as standalone markers — never connected by a line and never fed back
   * into the eHbA1c calculation, which only ever reads SensorGlucose/MeterGlucose. */
  const labChartPoints = $derived(
    labResults.map((r) => ({
      id: r.id,
      date: toDate(r.measuredAt),
      valuePercent: r.valuePercent ?? 0,
      displayValue: toDisplayUnit(r.valuePercent ?? 0),
      note: r.note,
    }))
  );

  async function addLabResult() {
    const value = Number(newLabValue);
    if (!newLabDate || !newLabValue || Number.isNaN(value)) return;
    savingLabResult = true;
    labResultError = null;
    try {
      await labResultsApi.create({
        measuredAt: new Date(`${newLabDate}T00:00:00.000Z`).toISOString() as unknown as Date,
        valuePercent: toPercentFromDisplayUnit(value),
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

<div class="@container space-y-6 p-3 @md:p-6">
  <Card.Root>
    <Card.Header class="flex flex-row flex-wrap items-start justify-between gap-4">
      <div>
        <Card.Title class="flex items-center gap-2">
          <Activity class="h-5 w-5 text-muted-foreground" />
          Estimated HbA1c (eHbA1c)
        </Card.Title>
        <Card.Description>
          eHbA1c estimates what a lab HbA1c test would read on a given day. For each day, it
          takes the average glucose from every reading in the trailing 90 days and weights each
          day's contribution by recency — the most recent ~30 days count for roughly half the
          estimate, tapering off exponentially for older days, similar to how glycated hemoglobin
          reflects glucose exposure over a red blood cell's ~90–120 day lifespan. That weighted
          average glucose is then converted to %HbA1c with the ADAG formula: HbA1c (%) = (average
          glucose in mg/dL + 46.7) / 28.7.
        </Card.Description>
      </div>
      <ToggleGroup.Root
        type="single"
        value={a1cUnit}
        onValueChange={(next: string) => {
          if (next === "percent" || next === "mmol") a1cUnit = next;
        }}
        class="shrink-0 rounded-md border bg-background p-0.5"
      >
        <ToggleGroup.Item value="percent" class="h-8 px-3 text-xs" aria-label="Show as percent">
          %
        </ToggleGroup.Item>
        <ToggleGroup.Item value="mmol" class="h-8 px-3 text-xs" aria-label="Show as mmol/mol">
          mmol/mol
        </ToggleGroup.Item>
      </ToggleGroup.Root>
    </Card.Header>
    <Card.Content>
      {#if loading}
        <div class="flex h-[320px] items-center justify-center text-muted-foreground">
          <Loader2 class="h-5 w-5 animate-spin mr-2" /> Loading eHbA1c timeline...
        </div>
      {:else if error}
        <div class="flex h-[320px] items-center justify-center text-destructive">
          Failed to load eHbA1c data. Please try again later.
        </div>
      {:else if chartData.length === 0}
        <div class="flex h-[320px] items-center justify-center text-muted-foreground">
          Not enough glucose history yet — each point needs at least a month of readings within
          the trailing 90 days.
        </div>
      {:else}
        {#if latest && extremes}
          <div class="mb-4 flex flex-wrap items-baseline gap-x-6 gap-y-1">
            <div>
              <span class="text-3xl font-semibold">{formatA1c(latest.estimatedA1cPercent)}</span>
              <span class="ml-2 text-sm text-muted-foreground">
                latest estimate ({formatLongDate(latest.date)})
              </span>
            </div>
            <div class="text-sm text-muted-foreground">
              Weighted mean glucose: {bg(latest.weightedAverageGlucoseMgdl)} {bgLabel()}
            </div>
            <div class="text-sm text-muted-foreground">
              Highest: <span class="font-medium text-foreground">{formatA1c(extremes.highest.estimatedA1cPercent)}</span>
              ({formatLongDate(extremes.highest.date)})
            </div>
            <div class="text-sm text-muted-foreground">
              Lowest: <span class="font-medium text-foreground">{formatA1c(extremes.lowest.estimatedA1cPercent)}</span>
              ({formatLongDate(extremes.lowest.date)})
            </div>
          </div>
        {/if}

        <div class="h-[320px] w-full @md:h-[400px]">
          <LineChart
            data={displayChartData}
            x="date"
            y="displayValue"
            {yDomain}
            clip
            series={[
              {
                key: "displayValue",
                label: a1cUnit === "percent" ? "eHbA1c %" : "eHbA1c mmol/mol",
                color: "var(--ehba1c-line)",
              },
            ]}
            props={{ spline: { "stroke-width": 3, "stroke-linecap": "round" } }}
            points={{ data: labChartPoints, x: (d) => d.date, y: (d) => d.displayValue, children: labMarkers }}
            {annotations}
          />
        </div>

        <div class="mt-4 flex flex-wrap gap-x-5 gap-y-2 text-sm">
          {#each zoneBands as band (band.key)}
            <div class="flex items-center gap-1.5">
              <span class="h-2.5 w-2.5 rounded-full" style="background-color: {band.swatch}"></span>
              <span>{band.label}</span>
              <span class="text-muted-foreground">({formatZoneRange(band)})</span>
            </div>
          {/each}
          {#if labChartPoints.length > 0}
            <div class="flex items-center gap-1.5">
              <span class="inline-block h-0 w-0 border-x-4 border-b-[7px] border-x-transparent border-b-foreground"
              ></span>
              <span>Lab result (not included in the calculation)</span>
            </div>
          {/if}
        </div>
      {/if}
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Lab results</Card.Title>
      <Card.Description>
        Enter a lab HbA1c result here — shown as a triangle marker on the chart above, so you
        can see how closely the eHbA1c estimate tracks an actual lab draw. Lab results are not
        included in the eHbA1c calculation itself. The date below is the date the blood was
        drawn, not the date you enter it here.
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
                  {formatA1c(result.valuePercent ?? 0)}
                  {#if result.note}<span class="text-muted-foreground font-normal"> — {result.note}</span>{/if}
                </div>
                <div class="text-xs text-muted-foreground">
                  {formatLongDate(toDate(result.measuredAt))}
                </div>
              </div>
              <Button
                variant="ghost"
                size="icon"
                aria-label="Delete lab result"
                onclick={() => (pendingDeleteLabResult = result)}
              >
                <Trash2 class="size-4" />
              </Button>
            </li>
          {/each}
        </ul>
      {/if}

      <div class="grid gap-3 sm:grid-cols-3">
        <div class="space-y-1.5">
          <Label for="lab-date">Date of blood draw</Label>
          <Input id="lab-date" type="date" bind:value={newLabDate} />
        </div>
        <div class="space-y-1.5">
          <Label for="lab-value">Result ({a1cUnit === "percent" ? "%" : "mmol/mol"})</Label>
          <Input id="lab-value" type="number" step="0.1" bind:value={newLabValue} placeholder={a1cUnit === "percent" ? "e.g. 7.0" : "e.g. 53"} />
        </div>
        <div class="space-y-1.5">
          <Label for="lab-note">Note (optional)</Label>
          <Input id="lab-note" type="text" bind:value={newLabNote} placeholder="e.g. GP lab" />
        </div>
      </div>

      {#if labResultError}
        <p class="text-destructive text-sm">{labResultError}</p>
      {/if}

      <Button onclick={addLabResult} disabled={savingLabResult || !newLabDate || !newLabValue}>
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

{#snippet labMarkers({ points }: { points: { x: number; y: number; data: (typeof labChartPoints)[number] }[] })}
  {#each points as point (point.data.id)}
    <polygon
      points="{point.x},{point.y - 7} {point.x - 6},{point.y + 5} {point.x + 6},{point.y + 5}"
      class="fill-foreground stroke-background"
      stroke-width="1"
    >
      <title>Lab result: {formatA1c(point.data.valuePercent)} ({formatLongDate(point.data.date)}){point.data.note ? ` — ${point.data.note}` : ""}</title>
    </polygon>
  {/each}
{/snippet}

<ConfirmDialog
  open={pendingDeleteLabResult !== null}
  onOpenChange={(o) => { if (!o) pendingDeleteLabResult = null; }}
  title="Delete this lab result?"
  confirmLabel="Delete"
  onConfirm={confirmDeleteLabResult}
>
  {#snippet description()}
    {#if pendingDeleteLabResult}
      Delete the {formatA1c(pendingDeleteLabResult.valuePercent ?? 0)} lab result from
      {formatLongDate(toDate(pendingDeleteLabResult.measuredAt))}.
    {/if}
  {/snippet}
</ConfirmDialog>
