<script lang="ts">
  import { Info } from "lucide-svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { cn } from "$lib/utils";
  import type { StatisticReliability } from "$lib/api";

  interface Props {
    reliability?: StatisticReliability | null;
    class?: string;
  }

  let { reliability, class: className }: Props = $props();
</script>

{#if reliability && reliability.meetsReliabilityCriteria === false}
  <Badge
    variant="warning"
    class={cn(
      "h-auto max-w-full items-start gap-1.5 whitespace-normal break-words py-1 text-left text-xs leading-snug",
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
