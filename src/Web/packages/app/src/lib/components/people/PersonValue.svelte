<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
  import { bg } from "#lib/utils/formatting.js";
  import { getReadState, isShownAsCurrent } from "./read-state.js";

  interface Props {
    person: TenantOverviewItem;
    size: "xs" | "sm" | "lg";
  }

  const { person, size }: Props = $props();

  const read = getReadState();
  const mgdl = $derived(person.latest?.mgdl);
</script>

{#if mgdl != null}
  <GlucoseValueIndicator
    displayValue={bg(mgdl)}
    variant={getGlucoseTileVariant(person.status)}
    isStale={!isShownAsCurrent(person, read)}
    isDisconnected={read.disconnected}
    {size}
  />
{:else}
  <GlucoseValueIndicator displayValue="—" variant="neutral" {size} />
{/if}
