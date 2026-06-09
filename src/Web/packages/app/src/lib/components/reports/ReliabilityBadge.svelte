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
    variant="outline"
    class={cn(
      "h-auto max-w-full items-start gap-1.5 whitespace-normal break-words border-amber-200 bg-amber-50 py-1 text-left text-[11px] leading-snug text-amber-600 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-400",
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
