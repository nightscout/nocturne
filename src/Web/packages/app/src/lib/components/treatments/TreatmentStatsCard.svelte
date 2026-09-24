<script lang="ts">
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import type { EntryCategoryId } from "$lib/constants/entry-categories";
  import type { TreatmentSummary } from "$lib/api";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";

  interface Props {
    treatmentSummary: TreatmentSummary;
    counts: Record<EntryCategoryId | "all", number>;
    /** Calendar days the selected range covers, counting both end days. */
    dayCount: number;
  }

  let { treatmentSummary, counts, dayCount }: Props = $props();

  const totalInsulin = $derived(
    (treatmentSummary.totals?.insulin?.bolus ?? 0) +
      (treatmentSummary.totals?.insulin?.basal ?? 0)
  );
  const totalCarbs = $derived(treatmentSummary.totals?.food?.carbs ?? 0);
  const bolusCount = $derived(counts.bolus);
  const carbEntriesCount = $derived(counts.carbs);

  let dailyAvgCarbs = $derived(totalCarbs / dayCount);
  let dailyAvgBoluses = $derived(bolusCount / dayCount);

  let avgInsulinPerBolus = $derived(
    bolusCount > 0 ? totalInsulin / bolusCount : 0
  );
  let avgCarbsPerEntry = $derived(
    carbEntriesCount > 0 ? totalCarbs / carbEntriesCount : 0
  );

  const categoryBreakdown = $derived(
    Object.values(ENTRY_CATEGORIES)
      .filter((cat) => counts[cat.id] > 0)
      .map((cat) => `${cat.name} ${counts[cat.id]}`)
      .join(" · ")
  );
</script>

<FigureStrip
  figures={[
    { label: "Total Records", value: String(counts.all), note: categoryBreakdown || undefined },
    {
      label: "Insulin",
      value: totalInsulin.toFixed(1),
      unit: "U",
      note: `${bolusCount} boluses · ${dailyAvgBoluses.toFixed(1)}/day · ${avgInsulinPerBolus.toFixed(1)}U avg`,
    },
    {
      label: "Carbs",
      value: totalCarbs.toFixed(0),
      unit: "g",
      note: `${carbEntriesCount} meals · ${dailyAvgCarbs.toFixed(0)}g/day · ${avgCarbsPerEntry.toFixed(0)}g avg/meal`,
    },
  ]}
/>
