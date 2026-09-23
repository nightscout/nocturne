<script lang="ts">
  import {
    formatInsulinDisplay,
    formatCarbDisplay,
    type TreatmentSummary,
  } from "$lib/utils/formatting";

  interface Props {
    treatmentSummary: TreatmentSummary;
  }

  let { treatmentSummary }: Props = $props();
</script>

<div class="@container mt-4 bg-blue-50 border border-blue-200 rounded-lg p-4">
  <h4 class="text-sm font-semibold text-foreground mb-3">Daily Summary</h4>
  <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4 text-sm">
    <div class="space-y-2">
      <div class="flex justify-between">
        <span class="text-muted-foreground">Bolus insulin:</span>
        <span class="font-medium">
          {formatInsulinDisplay(treatmentSummary?.totals?.insulin?.bolus)}U
        </span>
      </div>
      <div class="flex justify-between">
        <span class="text-muted-foreground">Total basal insulin:</span>
        <span class="font-medium">
          {formatInsulinDisplay(treatmentSummary?.totals?.insulin?.basal)}U
        </span>
      </div>
      <div class="flex justify-between border-t pt-2">
        <span class="text-foreground font-medium">Total daily insulin:</span>
        <span class="font-semibold">
          {formatInsulinDisplay((treatmentSummary?.totals?.insulin?.bolus ?? 0) + (treatmentSummary?.totals?.insulin?.basal ?? 0))}U
        </span>
      </div>
    </div>
    <div class="space-y-2">
      <div class="flex justify-between">
        <span class="text-muted-foreground">Total carbs:</span>
        <span class="font-medium">
          {formatCarbDisplay(treatmentSummary?.totals?.food?.carbs)} g
        </span>
      </div>
      <div class="flex justify-between">
        <span class="text-muted-foreground">Total protein:</span>
        <span class="font-medium">
          {formatCarbDisplay(treatmentSummary?.totals?.food?.protein)} g
        </span>
      </div>
      <div class="flex justify-between">
        <span class="text-muted-foreground">Total fat:</span>
        <span class="font-medium">
          {formatCarbDisplay(treatmentSummary?.totals?.food?.fat)} g
        </span>
      </div>
    </div>
  </div>
</div>
