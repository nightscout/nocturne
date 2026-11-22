<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import TrendArrow from "#lib/components/clock/TrendArrow.svelte";
  import { getDirectionInfo } from "#lib/utils/direction-info.svelte.js";
  import { deltaColorClass } from "@nocturne/ui/glucose";

  interface Props {
    person: TenantOverviewItem;
    /** Glyph size in px. */
    size: number;
  }

  const { person, size }: Props = $props();

  const direction = $derived(person.latest?.direction);
</script>

{#if direction}
  <span
    role="img"
    aria-label={getDirectionInfo(direction).label}
    class="inline-flex items-center {deltaColorClass(direction)}"
  >
    <TrendArrow {direction} {size} />
  </span>
{/if}
