<script lang="ts">
  import type { TreatmentFood } from "$lib/api";

  interface Props {
    food: TreatmentFood;
    class?: string;
  }

  let { food, class: className = "" }: Props = $props();

  const details = $derived.by(() => {
    const parts: string[] = [];

    // Only show portions if > 0
    if (food.portions) {
      parts.push(`${food.portions} portion${food.portions !== 1 ? "s" : ""}`);
    }

    // Always show carbs
    parts.push(`${food.carbs}g carbs`);

    // Show time offset if present
    if (food.timeOffsetMinutes) {
      parts.push(`+${food.timeOffsetMinutes} min`);
    }

    // Show note if present
    if (food.note) {
      parts.push(food.note);
    }

    return parts.join(" • ");
  });
</script>

<span class={className}>{details}</span>
