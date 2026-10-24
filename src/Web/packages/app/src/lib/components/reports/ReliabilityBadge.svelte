<script lang="ts">
  import Info from "@lucide/svelte/icons/info";
  import { Badge } from "$lib/components/ui/badge";
  import { cn } from "$lib/utils";
  import type { StatisticReliability, TimeZoneUnavailableReason } from "$lib/api";
  import TimeZoneNote from "./TimeZoneNote.svelte";

  interface Props {
    reliability?: StatisticReliability | null;
    timeZoneUnavailableReason?: TimeZoneUnavailableReason | null;
    class?: string;
  }

  let { reliability, timeZoneUnavailableReason, class: className }: Props = $props();

  const daysOfData = $derived(reliability?.daysOfData ?? 0);
</script>

{#if reliability && reliability.meetsReliabilityCriteria === false}
  <Badge
    variant="warning"
    class={cn(
      "h-auto max-w-full items-start whitespace-normal break-words text-left",
      className
    )}
  >
    <Info class="mt-px size-3 shrink-0" />
    <span class="min-w-0">
      Based on {daysOfData} {daysOfData === 1 ? "day" : "days"} of data ({reliability.recommendedMinimumDays ??
        14} recommended)
      {#if timeZoneUnavailableReason}
        <TimeZoneNote reason={timeZoneUnavailableReason}>Days of data are counted on UTC days.</TimeZoneNote>
      {/if}
    </span>
  </Badge>
{/if}
