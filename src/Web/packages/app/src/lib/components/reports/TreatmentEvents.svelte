<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import type { TreatmentDataItem } from "./types";

  interface Props {
    treatments: TreatmentDataItem[];
  }

  let { treatments }: Props = $props();
</script>

{#if treatments.length > 0}
  <div class="mt-4">
    <h4 class="text-sm font-semibold text-gray-700 mb-2">Treatment Events</h4>
    <div class="flex flex-wrap gap-2">
      {#each treatments as treatment}
        <div
          class="bg-blue-50 border border-blue-200 rounded px-2 py-1 text-xs"
        >
          <div class="font-medium">{treatment.eventType}</div>
          <div class="text-gray-600">
            {new Date(treatment.timestamp).toLocaleTimeString(formatLocale(), {
              hour: "2-digit",
              minute: "2-digit",
            })}
            {#if treatment.insulin}
              • {treatment.insulin}U{/if}
            {#if treatment.carbs}
              • {treatment.carbs}g carbs{/if}
          </div>
        </div>
      {/each}
    </div>
  </div>
{/if}
