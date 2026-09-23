<script lang="ts">
  import { Info } from "lucide-svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { cn } from "$lib/utils";
  import type { StatisticReliability } from "$lib/api";

  let { reliability, class: className } = $props<{
    reliability?: StatisticReliability | null;
    class?: string;
  }>();
</script>

{#if reliability && reliability.meetsReliabilityCriteria === false}
  <Badge
    variant="warning"
    class={cn(
      "h-auto max-w-full items-start gap-1.5 whitespace-normal break-words py-1 text-left text-[11px] leading-snug",
      className
    )}
  >
    <Info class="mt-px size-3 shrink-0" />
    <span class="min-w-0">
      Based on {reliability.daysOfData ?? 0} days of data ({reliability.recommendedMinimumDays ??
        14} recommended)
    </span>
  </Badge>
{/if}
