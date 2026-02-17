<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Bolus, CarbIntake, TreatmentSummary } from "$lib/api";
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

  import { getEventTypeStyle } from "$lib/constants/treatment-categories";
  import InsulinDonutChart from "$lib/components/reports/InsulinDonutChart.svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
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

  // Treatment colors
  const TREATMENT_COLORS = {
    carbs: "#ff9a00", // Orange
    insulin: "#0066cc", // Dark Blue
    bolus: "#0099ff", // Light Blue
    basal: "#66ccff", // Lighter Blue
  };

  // Process boluses and carb intakes for markers
  const treatmentMarkers = $derived.by(() => {
    const boluses = dayData?.boluses ?? [];
    const carbIntakes = dayData?.carbIntakes ?? [];

    const bolusMarkers = boluses
      .filter((b) => b.mills)
      .map((b) => ({
        time: new Date(b.mills!),
        carbs: 0,
        insulin: b.insulin ?? 0,
        eventType: b.bolusType ?? "Bolus",
        notes: "",
        rate: undefined as number | undefined,
        duration: b.duration,
        originalBolus: b,
        originalCarbIntake: undefined as CarbIntake | undefined,
      }));

    const carbMarkers = carbIntakes
      .filter((c) => c.mills)
      .map((c) => ({
        time: new Date(c.mills!),
        carbs: c.carbs ?? 0,
        insulin: 0,
        eventType: "Carb Intake",
        notes: "",
        rate: undefined as number | undefined,
        duration: undefined as number | undefined,
        originalBolus: undefined as Bolus | undefined,
        originalCarbIntake: c,
      }));

    return [...bolusMarkers, ...carbMarkers]
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

    if (!basicStats) {
      return {
        totalReadings: (dayData?.entries ?? []).length,
        mean: 0,
        median: 0,
        stdDev: 0,
        min: 0,
        max: 0,
        a1cEstimate: 0,
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
      a1cEstimate: analysis?.gmi?.value ?? 0,
      cv: analysis?.glycemicVariability?.coefficientOfVariation ?? 0,
    };
  });

  // Treatment statistics - uses backend-calculated values only
  // The backend TreatmentSummary is the source of truth
  const treatmentStats = $derived.by(() => {
    const summary = dayData?.treatmentSummary as TreatmentSummary | null;
    const treatmentCount = summary?.treatmentCount ?? ((dayData?.boluses?.length ?? 0) + (dayData?.carbIntakes?.length ?? 0));

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

  // Handle treatment row click
  function handleTreatmentClick(_treatment: (typeof treatmentMarkers)[0]) {
    // TODO: Bolus/CarbIntake edit dialog will be added with v4 CRUD endpoints
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

  <!-- Summary Stats -->
  <div class="grid md:grid-cols-3 gap-6">
    <!-- Glucose Overview -->
    <Card.Root class="md:col-span-2">
      <Card.Content class="p-4 space-y-4">
        <TIRStackedChart
          percentages={dayData?.analysis?.timeInRange?.percentages}
          orientation="horizontal"
          compact
        />
        <div class="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-3 text-sm">
          <div>
            <div class="text-muted-foreground">Mean</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(glucoseStats.mean, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Median</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(glucoseStats.median, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Std Dev</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(glucoseStats.stdDev, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">CV</div>
            <div class="font-medium tabular-nums">
              {glucoseStats.cv.toFixed(1)}%
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Range</div>
            <div class="font-medium tabular-nums">
              {formatGlucoseValue(glucoseStats.min, units)} – {formatGlucoseValue(glucoseStats.max, units)} {unitLabel}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">GMI</div>
            <div class="font-medium tabular-nums">
              {glucoseStats.a1cEstimate > 0 ? `${glucoseStats.a1cEstimate.toFixed(1)}%` : '–'}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Readings</div>
            <div class="font-medium tabular-nums">
              {glucoseStats.totalReadings}
            </div>
          </div>
        </div>
        <ReliabilityBadge reliability={dayData?.analysis?.reliability} />
      </Card.Content>
    </Card.Root>

    <!-- Treatment Summary -->
    <Card.Root>
      <Card.Content class="p-4 flex flex-col items-center gap-4">
        <InsulinDonutChart
          boluses={dayData?.boluses ?? []}
          basal={treatmentStats.totalBasal}
          href="/reports/insulin-delivery"
        />
        <div class="grid grid-cols-2 gap-x-6 gap-y-2 text-sm w-full">
          <div>
            <div class="text-muted-foreground">Total Carbs</div>
            <div class="font-bold tabular-nums">
              {treatmentStats.totalCarbs.toFixed(0)}g
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Treatments</div>
            <div class="font-medium tabular-nums">
              {treatmentStats.treatmentCount}
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

<!-- TODO: Bolus edit dialog will be added with v4 CRUD endpoints -->
