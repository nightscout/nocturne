<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Bolus, CarbIntake } from "$lib/api";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    ChevronLeft,
    ChevronRight,
    Calendar,
    ArrowLeft,
    Apple,
    ArrowUpDown,
    ArrowUp,
    ArrowDown,
    Edit,
    Filter,
    X,
  } from "lucide-svelte";
  import { getDayInReviewData } from "./data.remote";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
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

  // Get date from URL search params
  const today = new Date().toISOString().split("T")[0];
  const dateParam = $derived(
    page.url.searchParams.get("date") ?? today
  );

  // Create resource with automatic layout registration
  const dayDataResource = contextResource(
    () => getDayInReviewData(dateParam),
    { errorTitle: "Error Loading Day in Review" }
  );

  const dayData = $derived(dayDataResource.current);

  // Short aliases for deeply-nested backend data
  // Note: analysis, insulinDelivery, and treatmentSummary are currently null
  // until the statistics client is migrated to use summary/retrospective clients
  const analysis = $derived(dayData?.analysis as any);
  const basicStats = $derived(analysis?.basicStats);
  const delivery = $derived(dayData?.insulinDelivery as any);
  const summary = $derived(dayData?.treatmentSummary as any);

  // Parse current date from URL
  const currentDate = $derived(new Date(dateParam));

  // Treatments timeline filter/sort state
  let filterEventType = $state<string | null>(null);
  let sortColumn = $state<"time" | "type" | "carbs" | "insulin">("time");
  let sortDirection = $state<"asc" | "desc">("asc");

  // Date navigation
  function goToPreviousDay() {
    const prevDate = new Date(currentDate);
    prevDate.setDate(prevDate.getDate() - 1);
    goto(
      `/reports/day-in-review?date=${prevDate.toISOString().split("T")[0]}`,
      { invalidateAll: true }
    );
  }

  function goToNextDay() {
    const nextDate = new Date(currentDate);
    nextDate.setDate(nextDate.getDate() + 1);
    goto(
      `/reports/day-in-review?date=${nextDate.toISOString().split("T")[0]}`,
      { invalidateAll: true }
    );
  }

  function goBackToMonthView() {
    goto("/calendar");
  }

  // Format date for display
  const dateDisplay = $derived.by(() => {
    return currentDate.toLocaleDateString(undefined, {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });
  });

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
    const types = new Set<string>();
    for (const row of treatmentRows) {
      types.add(getRowLabel(row));
    }
    return Array.from(types).sort();
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

  // Insulin delivery values for the donut chart (fallback chain is meaningful)
  const scheduledBasal = $derived(
    delivery?.scheduledBasal ?? summary?.totals?.insulin?.scheduledBasal ?? 0
  );
  const additionalBasal = $derived(
    delivery?.additionalBasal ?? summary?.totals?.insulin?.additionalBasal ?? 0
  );

  // === Treatment Edit Dialog ===
  let editDialogOpen = $state(false);
  let editDialogRecord = $state<EntryRecord | null>(null);
  let editCorrelatedRecords = $state<EntryRecord[]>([]);

  function openBolusDialog(bolus: Bolus) {
    const record: EntryRecord = { kind: "bolus", data: bolus };
    const correlated: EntryRecord[] = [];
    if (bolus.correlationId) {
      const linkedCarb = (dayData?.carbIntakes ?? [] as CarbIntake[]).find(
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
        const linkedBolus = (dayData?.boluses ?? [] as Bolus[]).find(
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
    <Button
      variant="ghost"
      size="sm"
      class={alignRight ? "-mr-3" : "-ml-3"}
      onclick={() => toggleSort(column)}
    >
      {label}
      {#if sortColumn === column}
        {#if sortDirection === "asc"}
          <ArrowUp class="ml-1 h-4 w-4" />
        {:else}
          <ArrowDown class="ml-1 h-4 w-4" />
        {/if}
      {:else}
        <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
      {/if}
    </Button>
  </Table.Head>
{/snippet}

{#if dayDataResource.current}
<div class="space-y-6 p-4">
  <!-- Header with Navigation -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <Button variant="ghost" size="sm" onclick={goBackToMonthView}>
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back to Month View
        </Button>

        <div class="flex items-center gap-2">
          <Button variant="outline" size="icon" onclick={goToPreviousDay}>
            <ChevronLeft class="h-4 w-4" />
          </Button>
          <div class="flex items-center gap-2 min-w-[280px] justify-center">
            <Calendar class="h-4 w-4 text-muted-foreground" />
            <span class="text-lg font-medium">{dateDisplay}</span>
          </div>
          <Button variant="outline" size="icon" onclick={goToNextDay}>
            <ChevronRight class="h-4 w-4" />
          </Button>
        </div>

        <div class="w-[100px]"><!-- Spacer for alignment --></div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Summary Stats -->
  <div class="grid md:grid-cols-3 gap-6">
    <!-- Glucose Overview -->
    <Card.Root class="md:col-span-2">
      <Card.Content class="p-4 space-y-4">
        <TIRStackedChart
          percentages={analysis?.timeInRange?.percentages}
          orientation="horizontal"
          compact
        />
        <div class="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-3 text-sm">
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
              {(analysis?.glycemicVariability?.coefficientOfVariation ?? 0).toFixed(1)}%
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
        <ReliabilityBadge reliability={analysis?.reliability} />
      </Card.Content>
    </Card.Root>

    <!-- Treatment Summary -->
    <Card.Root>
      <Card.Content class="p-4 flex flex-col items-center gap-4">
        <InsulinDonutChart
          boluses={dayData?.boluses ?? []}
          {scheduledBasal}
          {additionalBasal}
          carbIntakes={dayData?.carbIntakes ?? []}
          onBolusClick={openBolusDialog}
        />
        <div class="grid grid-cols-2 gap-x-6 gap-y-2 text-sm w-full">
          <div>
            <div class="text-muted-foreground">Total Carbs</div>
            <div class="font-bold tabular-nums">
              {(summary?.totals?.food?.carbs ?? 0).toFixed(0)}g
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Boluses</div>
            <div class="font-medium tabular-nums">
              {delivery?.bolusCount ?? dayData?.boluses?.filter((b: Bolus) => (b.insulin ?? 0) > 0).length ?? 0}
            </div>
          </div>
        </div>
      </Card.Content>
    </Card.Root>
  </div>

  <!-- Main Glucose Chart with Treatment Markers -->
  <GlucoseChartCard
    dateRange={{
      from:
        dayData?.dateRange?.from ??
        new Date(
          currentDate.getFullYear(),
          currentDate.getMonth(),
          currentDate.getDate(),
          0, 0, 0
        ),
      to:
        dayData?.dateRange?.to ??
        new Date(
          currentDate.getFullYear(),
          currentDate.getMonth(),
          currentDate.getDate(),
          23, 59, 59
        ),
    }}
    showPredictions={hasApsSnapshots && selectedPredictionData != null}
    externalPredictionData={selectedPredictionData}
  />

  <!-- Historical Prediction Scrubber + APS State -->
  {#if hasApsSnapshots}
    <RetrospectiveTimeScrubber
      date={currentDate}
      bind:currentTime={scrubberTime}
      onTimeChange={handleScrubberTimeChange}
      stepMinutes={5}
    />
    <ApsStateCard snapshot={selectedSnapshot} />
  {/if}

  <!-- Treatments Timeline with Filter/Sort -->
  <Card.Root>
    <Card.Header class="pb-2">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <Card.Title class="flex items-center gap-2">
          <Apple class="h-5 w-5" />
          Treatments Timeline
        </Card.Title>

        <div class="flex items-center gap-2">
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
              {#each uniqueEventTypes as eventType}
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
      <Card.Description>Click on a treatment to edit it</Card.Description>
    </Card.Header>
    <Card.Content>
      {#if filteredTreatments.length > 0}
        <Table.Root>
          <Table.Header>
            <Table.Row>
              {@render sortableHeader("time", "Time")}
              {@render sortableHeader("type", "Type")}
              {@render sortableHeader("carbs", "Carbs", true)}
              {@render sortableHeader("insulin", "Insulin", true)}
              <Table.Head>Notes</Table.Head>
              <Table.Head class="w-[50px]"></Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each filteredTreatments as row}
              {@const style = getRowTypeStyle(row.rowType)}
              <Table.Row
                class="cursor-pointer hover:bg-muted/50 transition-colors"
                onclick={() => handleTreatmentClick(row)}
              >
                <Table.Cell class="font-medium">
                  {row.mills
                    ? new Date(row.mills).toLocaleTimeString(undefined, {
                        hour: "2-digit",
                        minute: "2-digit",
                      })
                    : "—"}
                </Table.Cell>
                <Table.Cell>
                  <Badge
                    variant="outline"
                    class="{style.colorClass} {style.bgClass} {style.borderClass}"
                  >
                    {getRowLabel(row)}
                  </Badge>
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if row.rowType === "carbIntake" && (row.carbs ?? 0) > 0}
                    <span class={getRowTypeStyle("carbIntake").colorClass}>
                      {row.carbs}g
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if row.rowType === "bolus" && (row.insulin ?? 0) > 0}
                    <span class={getRowTypeStyle("bolus").colorClass}>
                      {(row.insulin ?? 0).toFixed(2)}U
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell
                  class="text-muted-foreground truncate max-w-[200px]"
                >
                  —
                </Table.Cell>
                <Table.Cell>
                  <Button variant="ghost" size="icon" class="h-8 w-8">
                    <Edit class="h-4 w-4" />
                  </Button>
                </Table.Cell>
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
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
