<script lang="ts">
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import type { TreatmentLogCounts, TreatmentSummary } from "$lib/api";
  import { plural } from "$lib/utils/plural";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";

  interface Props {
    /** Absent when the filtered records hold no boluses or carb intakes. */
    treatmentSummary: TreatmentSummary | undefined;
    counts: TreatmentLogCounts;
  }

  let { treatmentSummary, counts }: Props = $props();

  const totalInsulin = $derived(treatmentSummary?.totals?.insulin?.bolus ?? 0);
  const totalCarbs = $derived(treatmentSummary?.totals?.food?.carbs ?? 0);

  const categoryBreakdown = $derived(
    Object.values(ENTRY_CATEGORIES)
      .filter((cat) => (counts[cat.id] ?? 0) > 0)
      .map((cat) => `${cat.name} ${counts[cat.id]}`)
      .join(" · ")
  );
</script>

<FigureStrip
  figures={[
    { label: "Total records", value: String(counts.all ?? 0), note: categoryBreakdown || undefined },
    {
      label: "Bolus insulin",
      value: totalInsulin.toFixed(1),
      unit: "U",
      note: `${plural(treatmentSummary?.bolusCount ?? 0, ["# bolus", "# boluses"])} · ${(treatmentSummary?.dailyBoluses ?? 0).toFixed(1)}/day · ${(treatmentSummary?.averagePerBolus ?? 0).toFixed(1)}U avg`,
    },
    {
      label: "Carbs",
      value: totalCarbs.toFixed(0),
      unit: "g",
      note: `${plural(treatmentSummary?.carbEntryCount ?? 0, ["# carb entry", "# carb entries"])} · ${(treatmentSummary?.dailyCarbs ?? 0).toFixed(0)}g/day · ${(treatmentSummary?.averageCarbsPerEntry ?? 0).toFixed(0)}g avg/entry`,
    },
  ]}
/>
