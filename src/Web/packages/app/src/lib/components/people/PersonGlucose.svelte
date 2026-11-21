<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import TrendArrow from "#lib/components/clock/TrendArrow.svelte";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
  import { bg, bgDelta, bgLabel } from "#lib/utils/formatting.js";
  import { getDirectionInfo } from "#lib/utils/direction-info.svelte.js";
  import { deltaColorClass } from "@nocturne/ui/glucose";
  import { readingIsNotCurrent } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
    size: "xs" | "lg";
    /** Set the unit after the delta, where no column header carries it. */
    deltaUnit?: boolean;
  }

  const { person, size, deltaUnit = false }: Props = $props();

  const latest = $derived(person.latest);
  const mgdl = $derived(latest?.mgdl);
  const notCurrent = $derived(readingIsNotCurrent(person));
  const arrowSize = $derived(size === "lg" ? 22 : 16);
  // The large layout states the unit after the delta. With no delta shown, the value keeps it.
  const unitFollowsDelta = $derived(
    deltaUnit && !notCurrent && latest?.delta != null
  );
  const unitClass = $derived.by(() => {
    if (size === "xs") return "text-xs text-muted-foreground @2xl:sr-only";
    return unitFollowsDelta ? "sr-only" : "text-xs text-muted-foreground";
  });
</script>

<div class="flex items-center gap-2">
  {#if mgdl != null}
    <GlucoseValueIndicator
      displayValue={bg(mgdl)}
      variant={getGlucoseTileVariant(person.status)}
      isStale={notCurrent}
      {size}
    />
    <span class={unitClass} data-testid="bg-unit">{bgLabel()}</span>
    {#if !notCurrent}
      {#if latest?.direction}
        <span
          role="img"
          aria-label={getDirectionInfo(latest.direction).label}
          class="inline-flex items-center {deltaColorClass(latest.direction)}"
        >
          <TrendArrow direction={latest.direction} size={arrowSize} />
        </span>
      {/if}
      {#if latest?.delta != null}
        <span
          class="text-sm text-muted-foreground tabular-nums"
          data-testid="bg-delta"
        >
          {bgDelta(latest.delta)}
          {#if deltaUnit}
            &nbsp;{bgLabel()}
          {:else}
            <span class="sr-only">{bgLabel()}</span>
          {/if}
        </span>
      {/if}
    {/if}
  {:else}
    <GlucoseValueIndicator displayValue="—" variant="neutral" {size} />
  {/if}
</div>
