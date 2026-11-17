<script lang="ts">
  import Info from "@lucide/svelte/icons/info";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Tooltip from "#lib/components/ui/tooltip/index.js";
  import { cn } from "#lib/utils/index.js";
  import type { StatisticReliability, TimeZoneUnavailableReason } from "#lib/api/index.js";
  import TimeZoneNote from "./TimeZoneNote.svelte";
  import { timeZoneReasonCopy } from "./time-zone-reasons.svelte";

  interface Props {
    reliability?: StatisticReliability | null;
    timeZoneUnavailableReason?: TimeZoneUnavailableReason | null;
    compact?: boolean;
    class?: string;
  }

  let { reliability, timeZoneUnavailableReason, compact = false, class: className }: Props = $props();

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
        {#if compact}
          {@const copy = timeZoneReasonCopy(timeZoneUnavailableReason)}
          <br />
          <Tooltip.Root>
            <Tooltip.Trigger variant="term">
              Counted on UTC days
              <Info class="size-3" aria-hidden="true" />
            </Tooltip.Trigger>
            <Tooltip.Content class="max-w-64 text-left">
              {copy.cause}
              {#if copy.remedy}
                {copy.remedy}
              {/if}
            </Tooltip.Content>
          </Tooltip.Root>
        {:else}
          <TimeZoneNote reason={timeZoneUnavailableReason}>Days of data are counted on UTC days.</TimeZoneNote>
        {/if}
      {/if}
    </span>
  </Badge>
{/if}
