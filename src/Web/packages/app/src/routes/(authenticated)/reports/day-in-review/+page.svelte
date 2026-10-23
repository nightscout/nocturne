<script lang="ts">
  import { timeDay } from "d3-time";
  import { distinct } from "$lib/utils/collections";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import type { Bolus, CarbIntake } from "$lib/api";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { cn } from "$lib/utils";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Calendar from "@lucide/svelte/icons/calendar";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Apple from "@lucide/svelte/icons/apple";
  import ArrowUpDown from "@lucide/svelte/icons/arrow-up-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import Edit from "@lucide/svelte/icons/square-pen";
  import Filter from "@lucide/svelte/icons/funnel";
  import X from "@lucide/svelte/icons/x";
  import { getDayInReviewData } from "./data.remote";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, formatLongDate, getUnitLabel, time } from "$lib/utils/formatting";
  import {
    getRowTypeStyle,
    mergeTreatmentRows,
    type TreatmentRow,
  } from "$lib/constants/treatment-categories";
  import InsulinDonutChart from "$lib/components/reports/InsulinDonutChart.svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import RetrospectiveTimeScrubber from "$lib/components/reports/RetrospectiveTimeScrubber.svelte";
  import ApsStateCard from "$lib/components/reports/ApsStateCard.svelte";
  import TreatmentEditDialog from "$lib/components/treatments/TreatmentEditDialog.svelte";
  import { GlucoseChartCard } from "$lib/components/dashboard/glucose-chart";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { apsSnapshotToPrediction } from "$lib/utils/aps-snapshot-to-prediction";
  import { endOfDay, isDayString, startOfDay, timeOnDay, toDayString } from "$lib/utils/date-range";
  import { setReportPrintMeta } from "$lib/components/reports/print/report-print.svelte";

  // Get date from URL search params. The default is the local calendar day —
  // taking it from `toISOString()` names yesterday for anyone east of UTC.
  const today = toDayString();
  const dateParam = $derived.by(() => {
    const fromUrl = page.url.searchParams.get("date");
    return isDayString(fromUrl) ? fromUrl : today;
  });

  setReportPrintMeta(() => ({ period: { from: dateParam, to: dateParam } }));

  // Create resource with automatic layout registration
  const dayDataResource = contextResource(
    () => getDayInReviewData(dateParam),
    { errorTitle: "Error Loading Day in Review" }
  );

  const dayData = $derived(dayDataResource.current);

  // Short aliases for deeply-nested backend data
  // Note: analysis, insulinDelivery, and treatmentSummary are currently null
  // until the statistics client is migrated to use summary/retrospective clients
  const analysis = $derived(dayData?.analysis);
  const basicStats = $derived(analysis?.basicStats);
  const delivery = $derived(dayData?.insulinDelivery);
  const summary = $derived(dayData?.treatmentSummary);

  // Parse current date from URL. Read as a local day, not as UTC midnight, which
  // renders as the previous day for anyone west of UTC.
  const currentDate = $derived(startOfDay(dateParam));

  // Treatments timeline filter/sort state
  let filterEventType = $state<string | null>(null);
  let sortColumn = $state<"time" | "type" | "carbs" | "insulin">("time");
  let sortDirection = $state<"asc" | "desc">("asc");

  // Date navigation
  function goToDayOffset(days: number) {
    const target = timeDay.offset(currentDate, days);
    goto(resolve(`/reports/day-in-review?date=${toDayString(target)}`), {
      invalidateAll: true,
      replaceState: true,
    });
  }

  function goBackToPreviousView() {
    if (window.history.length > 1) {
      window.history.back();
    } else {
      goto(resolve("/calendar"));
    }
  }

  // Format date for display
  const dateDisplay = $derived(formatLongDate(currentDate));

  // Get units preference
  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  // Merge boluses and carb intakes into a single timeline using existing utility
  const treatmentRows = $derived(
    mergeTreatmentRows(dayData?.boluses ?? [], dayData?.carbIntakes ?? [])
  );

  function getRowLabel(row: TreatmentRow): string {
    return row.rowType === "bolus" ? (row.bolusType ?? "Bolus") : "Carb Intake";
  }

  // Get unique event types for filter dropdown
  const uniqueEventTypes = $derived.by(() => {
    return distinct(treatmentRows.map(getRowLabel)).sort();
  });

  // Filtered and sorted treatments
  const filteredTreatments = $derived.by(() => {
    let result = [...treatmentRows];

    if (filterEventType) {
      result = result.filter((row) => getRowLabel(row) === filterEventType);
    }

    result.sort((a, b) => {
      let comparison = 0;
      switch (sortColumn) {
        case "time":
          comparison = (a.mills ?? 0) - (b.mills ?? 0);
          break;
        case "type":
          comparison = getRowLabel(a).localeCompare(getRowLabel(b));
          break;
        case "carbs": {
          const ac = a.rowType === "carbIntake" ? (a.carbs ?? 0) : 0;
          const bc = b.rowType === "carbIntake" ? (b.carbs ?? 0) : 0;
          comparison = ac - bc;
          break;
        }
        case "insulin": {
          const ai = a.rowType === "bolus" ? (a.insulin ?? 0) : 0;
          const bi = b.rowType === "bolus" ? (b.insulin ?? 0) : 0;
          comparison = ai - bi;
          break;
        }
      }
      return sortDirection === "asc" ? comparison : -comparison;
    });

    return result;
  });

  // === Treatment Edit Dialog ===
  let editDialogOpen = $state(false);
  let editDialogRecord = $state<EntryRecord | null>(null);
  let editCorrelatedRecords = $state<EntryRecord[]>([]);

  function openBolusDialog(bolus: Bolus) {
    const record: EntryRecord = { kind: "bolus", data: bolus };
    const correlated: EntryRecord[] = [];
    if (bolus.correlationId) {
      const linkedCarb = (dayData?.carbIntakes ?? []).find(
        (c: CarbIntake) => c.correlationId === bolus.correlationId
      );
      if (linkedCarb) correlated.push({ kind: "carbs", data: linkedCarb });
    }
    editDialogRecord = record;
    editCorrelatedRecords = correlated;
    editDialogOpen = true;
  }

  function handleTreatmentClick(row: TreatmentRow) {
    if (row.rowType === "bolus") {
      openBolusDialog(row);
    } else {
      editDialogRecord = { kind: "carbs", data: row };
      const correlated: EntryRecord[] = [];
      if (row.correlationId) {
        const linkedBolus = (dayData?.boluses ?? []).find(
          (b: Bolus) => b.correlationId === row.correlationId
        );
        if (linkedBolus) correlated.push({ kind: "bolus", data: linkedBolus });
      }
      editCorrelatedRecords = correlated;
      editDialogOpen = true;
    }
  }

  // Toggle sort
  function toggleSort(column: typeof sortColumn) {
    if (sortColumn === column) {
      sortDirection = sortDirection === "asc" ? "desc" : "asc";
    } else {
      sortColumn = column;
      sortDirection = "asc";
    }
  }

  // Clear filter
  function clearFilter() {
    filterEventType = null;
  }

  // === Historical Predictions ===
  const apsSnapshots = $derived(dayData?.apsSnapshots ?? []);
  const hasApsSnapshots = $derived(apsSnapshots.length > 0);

  // Scrubber time state
  let scrubberTime = $state(new Date());

  // Each day that loads starts the scrubber afresh: at `time` (HH:mm) when a link gave one, read
  // in the zone the day was loaded in, else now.
  // Primitives, so a refetch of the same day leaves a scrubbed position alone.
  const timeParam = $derived(page.url.searchParams.get("time"));
  const loadedDay = $derived(dayData?.date === dateParam ? dayData.date : null);
  const loadedZone = $derived(dayData?.timeZone);

  // The day's boundaries in the zone its data was loaded in; `currentDate` stays the viewer's
  // midnight, which only names the day.
  const dayZone = $derived(loadedDay ? loadedZone : undefined);
  const dayStart = $derived(startOfDay(dateParam, dayZone));
  const dayEnd = $derived(endOfDay(dateParam, dayZone));
  $effect(() => {
    if (!loadedDay) return;
    scrubberTime = timeOnDay(loadedDay, timeParam, loadedZone) ?? new Date();
  });

  // Find the nearest APS snapshot to the scrubber time
  const selectedSnapshot = $derived.by(() => {
    if (apsSnapshots.length === 0) return null;
    const targetMs = scrubberTime.getTime();
    let closest = apsSnapshots[0];
    let closestDist = Math.abs((closest.mills ?? 0) - targetMs);
    for (let i = 1; i < apsSnapshots.length; i++) {
      const dist = Math.abs((apsSnapshots[i].mills ?? 0) - targetMs);
      if (dist < closestDist) {
        closest = apsSnapshots[i];
        closestDist = dist;
      }
    }
    return closest;
  });

  const selectedPredictionData = $derived.by(() => {
    if (!selectedSnapshot) return null;
    return apsSnapshotToPrediction(selectedSnapshot);
  });

  function handleScrubberTimeChange(time: Date) {
    scrubberTime = time;
  }
</script>

{#snippet sortableHeader(column: "time" | "type" | "carbs" | "insulin", label: string, alignRight = false)}
  <Table.Head class={alignRight ? "text-right" : ""}>
    <span class="hidden print:inline">{label}</span>
    <Button
      variant="ghost"
      size="sm"
      class={[alignRight ? "-mr-3" : "-ml-3", "print:hidden"]}
      onclick={() => toggleSort(column)}
    >
      {label}
      {#if sortColumn === column}
        {#if sortDirection === "asc"}
          <ArrowUp class="ml-1 h-4 w-4 print:hidden" />
        {:else}
          <ArrowDown class="ml-1 h-4 w-4 print:hidden" />
        {/if}
      {:else}
        <ArrowUpDown class="ml-1 h-4 w-4 opacity-50 print:hidden" />
      {/if}
    </Button>
  </Table.Head>
{/snippet}

{#if dayDataResource.current}
<div class="@container space-y-6">
  <div
    class="flex flex-col gap-3 print:hidden @2xl:flex-row @2xl:flex-wrap @2xl:items-center @2xl:justify-between"
  >
    <Button
      variant="ghost"
      size="sm"
      class="self-start @2xl:self-auto"
      onclick={goBackToPreviousView}
    >
      <ArrowLeft class="h-4 w-4 mr-2" />
      Back to Previous View
    </Button>

    <div class="flex items-center justify-center gap-2">
      <Button
        variant="outline"
        size="icon"
        class="shrink-0"
        onclick={() => goToDayOffset(-1)}
      >
        <ChevronLeft class="h-4 w-4" />
      </Button>
      <div
        class="flex min-w-0 items-center justify-center gap-2 px-1 @2xl:min-w-[220px]"
      >
        <Calendar class="h-4 w-4 shrink-0 text-muted-foreground" />
        <span class="truncate text-base font-medium @2xl:text-lg">
          {dateDisplay}
        </span>
      </div>
      <Button
        variant="outline"
        size="icon"
        class="shrink-0"
        onclick={() => goToDayOffset(1)}
      >
        <ChevronRight class="h-4 w-4" />
      </Button>
    </div>

    <div class="hidden @2xl:block @2xl:w-[100px]"><!-- Spacer for alignment --></div>
  </div>

  <div class="grid @4xl:grid-cols-3 print:grid-cols-3 gap-6">
      <Card.Root class="@4xl:col-span-2 print:col-span-2">
      <Card.Content class="p-4 space-y-4">
        <TIRStackedChart
          percentages={analysis?.timeInRange?.percentages}
          orientation="horizontal"
          keyOnScreen
          compact
        />
        <div class="grid grid-cols-2 @lg:grid-cols-4 gap-x-6 gap-y-3 text-sm">
          <div>
            <div class="text-muted-foreground">Mean</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(basicStats?.mean ?? 0, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Median</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(basicStats?.median ?? 0, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Std Dev</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(basicStats?.standardDeviation ?? 0, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">CV</div>
            <div class="font-medium tabular-nums">
              {analysis?.glycemicVariability?.coefficientOfVariation?.toFixed(1) ??
                "–"}%
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Range</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(basicStats?.min ?? 0, units)} – {formatGlucoseValue(basicStats?.max ?? 0, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">GMI</div>
            <div class="font-medium tabular-nums">
              {analysis?.gmi?.value ? `${analysis.gmi.value.toFixed(1)}%` : '–'}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Readings</div>
            <div class="font-medium tabular-nums">
              {basicStats?.count ?? dayData?.entries?.length ?? 0}
            </div>
          </div>
        </div>
        <ReliabilityBadge reliability={analysis?.reliability} class="print:hidden" />
      </Card.Content>
    </Card.Root>

      <Card.Root>
      <Card.Content class="p-4 flex flex-col items-center gap-4">
        {#key dateParam}
          <InsulinDonutChart
            boluses={delivery?.manualBoluses ?? []}
            totalBasal={delivery?.totalBasal ?? 0}
            totalBolus={delivery?.totalBolus ?? 0}
            manualBolus={delivery?.manualBolus ?? 0}
            automaticBolus={delivery?.automaticBolus ?? 0}
            scheduledBasal={delivery?.scheduledBasal ?? 0}
            additionalBasal={delivery?.additionalBasal ?? 0}
            totalInsulin={delivery?.totalInsulin ?? 0}
            carbIntakes={dayData?.carbIntakes ?? []}
            onBolusClick={openBolusDialog}
          />
        {/key}
        <div class="grid grid-cols-2 gap-x-6 gap-y-2 text-sm w-full">
          <div>
            <div class="text-muted-foreground">Total Carbs</div>
            <div class="font-medium tabular-nums">
              {(summary?.totals?.food?.carbs ?? 0).toFixed(0)}g
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Manual boluses</div>
            <div class="font-medium tabular-nums">
              {delivery?.manualBolusCount ?? 0}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Automatic boluses</div>
            <div class="font-medium tabular-nums">
              {delivery?.automaticBolusCount ?? 0}
            </div>
          </div>
        </div>
      </Card.Content>
    </Card.Root>
  </div>

  <GlucoseChartCard
    dateRange={{
      from: dayData?.dateRange?.from ?? dayStart,
      to: dayData?.dateRange?.to ?? dayEnd,
    }}
    showPredictions={hasApsSnapshots && selectedPredictionData != null}
    externalPredictionData={selectedPredictionData}
  />

  <!-- Both follow the scrubber, which paper cannot move. -->
  {#if hasApsSnapshots}
    <div class="space-y-6 print:hidden">
      <RetrospectiveTimeScrubber
        date={dayStart}
        timeZone={dayZone}
        bind:currentTime={scrubberTime}
        onTimeChange={handleScrubberTimeChange}
        stepMinutes={5}
      />
      <ApsStateCard snapshot={selectedSnapshot} />
    </div>
  {/if}

  <Card.Root>
    <Card.Header class="pb-2">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <Card.Title class="flex items-center gap-2">
          <Apple class="h-5 w-5" />
          Treatments Timeline
        </Card.Title>

        <div class="flex items-center gap-2 print:hidden">
          <Select.Root
            type="single"
            value={filterEventType ?? ""}
            onValueChange={(v) => {
              filterEventType = v === "" ? null : v;
            }}
          >
            <Select.Trigger class="w-[180px]">
              <div class="flex items-center gap-2">
                <Filter class="h-4 w-4" />
                {filterEventType || "All Types"}
              </div>
            </Select.Trigger>
            <Select.Content>
              <Select.Item value="">All Types</Select.Item>
              {#each uniqueEventTypes as eventType (eventType)}
                <Select.Item value={eventType}>{eventType}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>

          {#if filterEventType}
            <Button variant="ghost" size="icon" onclick={clearFilter}>
              <X class="h-4 w-4" />
            </Button>
          {/if}
        </div>
      </div>
      <Card.Description class="print:hidden">Click on a treatment to edit it</Card.Description>
      {#if filterEventType}
        <p class="hidden text-sm text-muted-foreground print:block">Showing {filterEventType} only</p>
      {/if}
    </Card.Header>
    <Card.Content>
      {#if filteredTreatments.length > 0}
        <div class="overflow-x-auto print:overflow-visible">
        <Table.Root>
          <Table.Header>
            <Table.Row>
              {@render sortableHeader("time", "Time")}
              {@render sortableHeader("type", "Type")}
              {@render sortableHeader("carbs", "Carbs", true)}
              {@render sortableHeader("insulin", "Insulin", true)}
              <Table.Head class="w-[50px] print:hidden"></Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each filteredTreatments as row (`${row.rowType}-${row.id}`)}
              {@const style = getRowTypeStyle(row.rowType)}
              <Table.Row
                class="cursor-pointer"
                onclick={() => handleTreatmentClick(row)}
              >
                <Table.Cell class="font-medium">
                  {row.mills ? time(row.mills) : "—"}
                </Table.Cell>
                <Table.Cell>
                  <Badge variant={style.badge}>
                    {getRowLabel(row)}
                  </Badge>
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if row.rowType === "carbIntake" && (row.carbs ?? 0) > 0}
                    <span class={cn(getRowTypeStyle("carbIntake").colorClass, "print:text-foreground")}>
                      {row.carbs}g
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if row.rowType === "bolus" && (row.insulin ?? 0) > 0}
                    <span class={cn(getRowTypeStyle("bolus").colorClass, "print:text-foreground")}>
                      {(row.insulin ?? 0).toFixed(2)}U
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell class="print:hidden">
                  <Button variant="ghost" size="icon-sm" aria-label="Edit treatment">
                    <Edit class="h-4 w-4" />
                  </Button>
                </Table.Cell>
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
        </div>
      {:else}
        <p class="text-center text-muted-foreground py-8">
          {filterEventType
            ? `No ${filterEventType} treatments found for this day`
            : "No treatments recorded for this day"}
        </p>
      {/if}
    </Card.Content>
  </Card.Root>
</div>

<TreatmentEditDialog
  bind:open={editDialogOpen}
  record={editDialogRecord}
  correlatedRecords={editCorrelatedRecords}
  onClose={() => { editDialogOpen = false; }}
  onSave={() => { editDialogOpen = false; }}
/>
{/if}
