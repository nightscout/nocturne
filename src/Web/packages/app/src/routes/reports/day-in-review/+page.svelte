<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Treatment, TreatmentSummary } from "$lib/api";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import * as Select from "$lib/components/ui/select";
  import Button from "$lib/components/ui/button/button.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import {
    ChevronLeft,
    ChevronRight,
    Calendar,
    ArrowLeft,
    Pill,
    Apple,
    Droplet,
    Target,
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

  import { TreatmentEditDialog } from "$lib/components/treatments";
  import { getEventTypeStyle } from "$lib/constants/treatment-categories";
  import InsulinDonutChart from "$lib/components/reports/InsulinDonutChart.svelte";
  import { GlucoseChartCard } from "$lib/components/dashboard/glucose-chart";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get date from URL search params
  const dateParam = $derived(
    page.url.searchParams.get("date") ?? new Date().toISOString().split("T")[0]
  );

  // Create resource with automatic layout registration
  const dayDataResource = contextResource(
    () => getDayInReviewData(dateParam),
    { errorTitle: "Error Loading Day in Review" }
  );

  const dayData = $derived(dayDataResource.current);

  // Parse current date from URL
  const currentDate = $derived(new Date(dateParam));

  // Treatment editing state
  let selectedTreatment = $state<Treatment | null>(null);
  let editDialogOpen = $state(false);

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
      {
        invalidateAll: true,
      }
    );
  }

  function goToNextDay() {
    const nextDate = new Date(currentDate);
    nextDate.setDate(nextDate.getDate() + 1);
    goto(
      `/reports/day-in-review?date=${nextDate.toISOString().split("T")[0]}`,
      {
        invalidateAll: true,
      }
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

  // Colors for glucose chart (using CSS variables for theme support)
  const GLUCOSE_COLORS = {
    line: "var(--insulin)",
    low: "var(--glucose-low)",
    inRange: "var(--glucose-in-range)",
    high: "var(--glucose-high)",
    severeLow: "var(--glucose-very-low)",
    severeHigh: "var(--glucose-very-high)",
  };

  // Treatment colors
  const TREATMENT_COLORS = {
    carbs: "#ff9a00", // Orange
    insulin: "#0066cc", // Dark Blue
    bolus: "#0099ff", // Light Blue
    basal: "#66ccff", // Lighter Blue
  };

  // Process treatments for markers (with original treatment reference for editing)
  const treatmentMarkers = $derived.by(() => {
    const treatments = (dayData?.treatments ?? []) as Treatment[];

    return treatments
      .filter((t) => t.mills || t.createdAt || t.created_at)
      .map((t) => ({
        time: new Date(
          t.mills ?? new Date(t.createdAt ?? t.created_at!).getTime()
        ),
        carbs: t.carbs ?? 0,
        insulin: t.insulin ?? 0,
        eventType: t.eventType ?? "",
        notes: t.notes ?? "",
        rate: t.rate,
        duration: t.duration,
        original: t, // Keep reference for editing
      }))
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  // Get unique event types for filter dropdown
  const uniqueEventTypes = $derived.by(() => {
    const types = new Set<string>();
    for (const t of treatmentMarkers) {
      if (t.eventType) types.add(t.eventType);
    }
    return Array.from(types).sort();
  });

  // Filtered and sorted treatments
  const filteredTreatments = $derived.by(() => {
    let result = [...treatmentMarkers];

    // Apply filter
    if (filterEventType) {
      result = result.filter((t) => t.eventType === filterEventType);
    }

    // Apply sort
    result.sort((a, b) => {
      let comparison = 0;
      switch (sortColumn) {
        case "time":
          comparison = a.time.getTime() - b.time.getTime();
          break;
        case "type":
          comparison = (a.eventType || "").localeCompare(b.eventType || "");
          break;
        case "carbs":
          comparison = a.carbs - b.carbs;
          break;
        case "insulin":
          comparison = a.insulin - b.insulin;
          break;
      }
      return sortDirection === "asc" ? comparison : -comparison;
    });

    return result;
  });

  // Use backend-calculated glucose statistics from analysis
  const glucoseStats = $derived.by(() => {
    const analysis = dayData?.analysis;
    const basicStats = analysis?.basicStats;
    const tir = analysis?.timeInRange?.percentages;

    if (!basicStats || !tir) {
      return {
        totalReadings: (dayData?.entries ?? []).length,
        mean: 0,
        median: 0,
        stdDev: 0,
        min: 0,
        max: 0,
        inRange: 0,
        low: 0,
        high: 0,
        inRangePercent: 0,
        lowPercent: 0,
        highPercent: 0,
        a1cEstimate: 0,
        gmi: 0,
        cv: 0,
      };
    }

    return {
      totalReadings: basicStats.count ?? (dayData?.entries ?? []).length,
      mean: basicStats.mean ?? 0,
      median: basicStats.median ?? 0,
      stdDev: basicStats.standardDeviation ?? 0,
      min: basicStats.min ?? 0,
      max: basicStats.max ?? 0,
      inRange: 0, // Count not available from backend
      low: 0,
      high: 0,
      inRangePercent: tir.target ?? 0,
      lowPercent: (tir.low ?? 0) + (tir.severeLow ?? 0),
      highPercent: (tir.high ?? 0) + (tir.severeHigh ?? 0),
      a1cEstimate:
        analysis?.gmi?.value ?? ((basicStats.mean ?? 0) + 46.7) / 28.7,
      gmi: analysis?.gmi?.value ?? 0,
      cv: analysis?.glycemicVariability?.coefficientOfVariation ?? 0,
    };
  });

  // Treatment statistics - uses backend-calculated values only
  // The backend TreatmentSummary is the source of truth
  const treatmentStats = $derived.by(() => {
    const summary = dayData?.treatmentSummary as TreatmentSummary | null;
    const treatments = (dayData?.treatments ?? []) as Treatment[];
    const treatmentCount = summary?.treatmentCount ?? treatments.length;

    // All totals come from backend calculation
    const totalBolus = summary?.totals?.insulin?.bolus ?? 0;
    const totalBasal = summary?.totals?.insulin?.basal ?? 0;
    const totalCarbs = summary?.totals?.food?.carbs ?? 0;
    const totalInsulin = totalBolus + totalBasal;

    return {
      totalCarbs,
      totalBolus,
      totalBasal,
      totalInsulin,
      totalDailyInsulin: totalInsulin,
      treatmentCount,
    };
  });

  // Pie chart data for TIR distribution
  const tirPieData = $derived(
    [
      {
        name: "In Range",
        value: glucoseStats.inRangePercent,
        color: GLUCOSE_COLORS.inRange,
      },
      {
        name: "Low",
        value: glucoseStats.lowPercent,
        color: GLUCOSE_COLORS.low,
      },
      {
        name: "High",
        value: glucoseStats.highPercent,
        color: GLUCOSE_COLORS.high,
      },
    ].filter((d) => d.value > 0)
  );

  // Handle treatment row click
  function handleTreatmentClick(treatment: (typeof treatmentMarkers)[0]) {
    selectedTreatment = treatment.original;
    editDialogOpen = true;
  }

  // Handle treatment save
  function handleTreatmentSave(updatedTreatment: Treatment) {
    // TODO: Call API to update treatment
    console.log("Saving treatment:", updatedTreatment);
    editDialogOpen = false;
    selectedTreatment = null;
    // Refresh data after save
    // getDayInReviewData.invalidate();
  }

  // Handle treatment dialog close
  function handleDialogClose() {
    editDialogOpen = false;
    selectedTreatment = null;
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
</script>

{#if dayDataResource.current}
<div class="space-y-6 p-4">
  <!-- Header with Navigation -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <!-- Back button -->
        <Button variant="ghost" size="sm" onclick={goBackToMonthView}>
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back to Month View
        </Button>

        <!-- Date Navigation -->
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

  <!-- Summary Stats Row -->
  <div class="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
    <!-- Time in Range -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.inRange}">
          {glucoseStats.inRangePercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time in Range</div>
      </Card.Content>
    </Card.Root>

    <!-- Low -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.low}">
          {glucoseStats.lowPercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time Low</div>
      </Card.Content>
    </Card.Root>

    <!-- High -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.high}">
          {glucoseStats.highPercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time High</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Carbs -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {TREATMENT_COLORS.carbs}">
          {treatmentStats.totalCarbs.toFixed(0)}g
        </div>
        <div class="text-sm text-muted-foreground">Total Carbs</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Bolus -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {TREATMENT_COLORS.bolus}">
          {treatmentStats.totalBolus.toFixed(1)}U
        </div>
        <div class="text-sm text-muted-foreground">Bolus Insulin</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Daily Insulin Donut Chart -->
    <Card.Root>
      <Card.Content class="p-4 flex justify-center">
        <InsulinDonutChart
          treatments={dayData?.treatments ?? []}
          basal={treatmentStats.totalBasal}
          href="/reports/insulin-delivery"
        />
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
          0,
          0,
          0
        ),
      to:
        dayData?.dateRange?.to ??
        new Date(
          currentDate.getFullYear(),
          currentDate.getMonth(),
          currentDate.getDate(),
          23,
          59,
          59
        ),
    }}
    showPredictions={false}
  />

  <!-- Detailed Stats Row -->
  <div class="grid md:grid-cols-3 gap-6">
    <!-- TIR Distribution Pie -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Target class="h-5 w-5" />
          Glucose Distribution
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <div class="h-[200px] flex items-center justify-center">
          {#if tirPieData.length > 0}
            {@const radius = 80}
            {@const innerRadius = 50}
            <svg
              width={radius * 2 + 20}
              height={radius * 2 + 20}
              viewBox="-{radius + 10} -{radius + 10} {radius * 2 + 20} {radius *
                2 +
                20}"
            >
              {#each tirPieData as slice, i}
                {@const total = tirPieData.reduce((sum, d) => sum + d.value, 0)}
                {@const startAngle =
                  tirPieData
                    .slice(0, i)
                    .reduce((sum, d) => sum + (d.value / total) * 360, 0) - 90}
                {@const endAngle = startAngle + (slice.value / total) * 360}
                {@const startRad = (startAngle * Math.PI) / 180}
                {@const endRad = (endAngle * Math.PI) / 180}
                {@const largeArc = endAngle - startAngle > 180 ? 1 : 0}
                {@const x1Outer = radius * Math.cos(startRad)}
                {@const y1Outer = radius * Math.sin(startRad)}
                {@const x2Outer = radius * Math.cos(endRad)}
                {@const y2Outer = radius * Math.sin(endRad)}
                {@const x1Inner = innerRadius * Math.cos(endRad)}
                {@const y1Inner = innerRadius * Math.sin(endRad)}
                {@const x2Inner = innerRadius * Math.cos(startRad)}
                {@const y2Inner = innerRadius * Math.sin(startRad)}
                <path
                  d="M {x1Outer} {y1Outer} A {radius} {radius} 0 {largeArc} 1 {x2Outer} {y2Outer} L {x1Inner} {y1Inner} A {innerRadius} {innerRadius} 0 {largeArc} 0 {x2Inner} {y2Inner} Z"
                  fill={slice.color}
                  stroke="white"
                  stroke-width="2"
                />
              {/each}
            </svg>
          {:else}
            <span class="text-muted-foreground">No data</span>
          {/if}
        </div>

        <!-- Legend -->
        <div class="flex justify-center gap-4 mt-4">
          {#each tirPieData as slice}
            <div class="flex items-center gap-2">
              <div
                class="h-3 w-3 rounded-full"
                style="background-color: {slice.color}"
              ></div>
              <span class="text-sm">
                {slice.name}: {slice.value.toFixed(1)}%
              </span>
            </div>
          {/each}
        </div>
      </Card.Content>
    </Card.Root>

    <!-- Glucose Statistics -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Droplet class="h-5 w-5" />
          Glucose Statistics
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <Table.Root>
          <Table.Body>
            <Table.Row>
              <Table.Cell class="font-medium">Mean</Table.Cell>
              <Table.Cell class="text-right">
                {formatGlucoseValue(glucoseStats.mean, units)}
                {unitLabel}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Median</Table.Cell>
              <Table.Cell class="text-right">
                {formatGlucoseValue(glucoseStats.median, units)}
                {unitLabel}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Std Dev</Table.Cell>
              <Table.Cell class="text-right">
                {formatGlucoseValue(glucoseStats.stdDev, units)}
                {unitLabel}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Min / Max</Table.Cell>
              <Table.Cell class="text-right">
                {formatGlucoseValue(glucoseStats.min, units)} / {formatGlucoseValue(
                  glucoseStats.max,
                  units
                )}
                {unitLabel}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Readings</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.totalReadings}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Est. A1c</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.a1cEstimate.toFixed(1)}%
              </Table.Cell>
            </Table.Row>
          </Table.Body>
        </Table.Root>
      </Card.Content>
    </Card.Root>

    <!-- Insulin Statistics -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Pill class="h-5 w-5" />
          Insulin & Carbs
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <Table.Root>
          <Table.Body>
            <Table.Row>
              <Table.Cell class="font-medium">Bolus Insulin</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.totalBolus.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Basal Insulin</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.totalBasal.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Total Daily Insulin</Table.Cell>
              <Table.Cell class="text-right font-bold">
                {treatmentStats.totalDailyInsulin.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Total Carbs</Table.Cell>
              <Table.Cell class="text-right font-bold">
                {treatmentStats.totalCarbs.toFixed(0)}g
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Treatment Count</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.treatmentCount}
              </Table.Cell>
            </Table.Row>
          </Table.Body>
        </Table.Root>
      </Card.Content>
    </Card.Root>
  </div>

  <!-- Treatments Timeline with Filter/Sort -->
  <Card.Root>
    <Card.Header class="pb-2">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <Card.Title class="flex items-center gap-2">
          <Apple class="h-5 w-5" />
          Treatments Timeline
        </Card.Title>

        <!-- Filter/Sort Controls -->
        <div class="flex items-center gap-2">
          <!-- Event Type Filter -->
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
              <Table.Head>
                <Button
                  variant="ghost"
                  size="sm"
                  class="-ml-3"
                  onclick={() => toggleSort("time")}
                >
                  Time
                  {#if sortColumn === "time"}
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
              <Table.Head>
                <Button
                  variant="ghost"
                  size="sm"
                  class="-ml-3"
                  onclick={() => toggleSort("type")}
                >
                  Type
                  {#if sortColumn === "type"}
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
              <Table.Head class="text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  class="-mr-3"
                  onclick={() => toggleSort("carbs")}
                >
                  Carbs
                  {#if sortColumn === "carbs"}
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
              <Table.Head class="text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  class="-mr-3"
                  onclick={() => toggleSort("insulin")}
                >
                  Insulin
                  {#if sortColumn === "insulin"}
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
              <Table.Head>Notes</Table.Head>
              <Table.Head class="w-[50px]"></Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each filteredTreatments as treatment}
              {@const style = getEventTypeStyle(treatment.eventType)}
              <Table.Row
                class="cursor-pointer hover:bg-muted/50 transition-colors"
                onclick={() => handleTreatmentClick(treatment)}
              >
                <Table.Cell class="font-medium">
                  {treatment.time.toLocaleTimeString(undefined, {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}
                </Table.Cell>
                <Table.Cell>
                  <Badge
                    variant="outline"
                    class="{style.colorClass} {style.bgClass} {style.borderClass}"
                  >
                    {treatment.eventType || "—"}
                  </Badge>
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if treatment.carbs > 0}
                    <span style="color: {TREATMENT_COLORS.carbs}">
                      {treatment.carbs}g
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if treatment.insulin > 0}
                    <span style="color: {TREATMENT_COLORS.bolus}">
                      {treatment.insulin.toFixed(2)}U
                    </span>
                  {:else if treatment.rate !== undefined}
                    <span style="color: {TREATMENT_COLORS.basal}">
                      {treatment.rate.toFixed(2)}U/hr
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell
                  class="text-muted-foreground truncate max-w-[200px]"
                >
                  {treatment.notes || "—"}
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
{/if}

<!-- Treatment Edit Dialog -->
<TreatmentEditDialog
  bind:open={editDialogOpen}
  treatment={selectedTreatment}
  availableEventTypes={uniqueEventTypes}
  onClose={handleDialogClose}
  onSave={handleTreatmentSave}
/>
