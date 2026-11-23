<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { bgDelta, bgLabel } from "#lib/utils/formatting.js";
  import PersonTrend from "./PersonTrend.svelte";
  import PersonValue from "./PersonValue.svelte";
  import { getReadState, isShownAsCurrent } from "./read-state.js";

  interface Props {
    person: TenantOverviewItem;
    size: "xs" | "lg";
    /** Set the unit after the delta, where no column header carries it. */
    deltaUnit?: boolean;
  }

  const { person, size, deltaUnit = false }: Props = $props();

  const latest = $derived(person.latest);
  const read = getReadState();
  const notCurrent = $derived(!isShownAsCurrent(person, read));
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
  <PersonValue {person} {size} />
  {#if latest?.mgdl != null}
    <span class={unitClass} data-testid="bg-unit">{bgLabel()}</span>
    {#if !notCurrent}
      <PersonTrend {person} size={arrowSize} />
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
  {/if}
</div>
