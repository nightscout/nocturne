<script lang="ts">
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import type { EntryCategoryId } from "$lib/constants/entry-categories";
  import type { TreatmentSummary } from "$lib/api";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";

  interface Props {
    treatmentSummary: TreatmentSummary;
    counts: Record<EntryCategoryId | "all", number>;
  }

  let { treatmentSummary, counts }: Props = $props();

  const totalInsulin = $derived(treatmentSummary.totals?.insulin?.bolus ?? 0);
  const totalCarbs = $derived(treatmentSummary.totals?.food?.carbs ?? 0);

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
      note: `${treatmentSummary.bolusCount ?? 0} boluses · ${(treatmentSummary.dailyBoluses ?? 0).toFixed(1)}/day · ${(treatmentSummary.averagePerBolus ?? 0).toFixed(1)}U avg`,
    },
    {
      label: "Carbs",
      value: totalCarbs.toFixed(0),
      unit: "g",
      note: `${treatmentSummary.carbEntryCount ?? 0} meals · ${(treatmentSummary.dailyCarbs ?? 0).toFixed(0)}g/day · ${(treatmentSummary.averageCarbsPerEntry ?? 0).toFixed(0)}g avg/meal`,
    },
  ]}
/>
