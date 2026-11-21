<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import {
    formatInsulinDisplay,
    relativeAge,
    toDate,
  } from "#lib/utils/formatting.js";
  import { readingIsNotCurrent } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const therapy = $derived(person.therapy);
  const showIob = $derived(!!therapy?.iobKnown && therapy.iob != null);
  const showCob = $derived(!!therapy?.cobKnown && therapy.cob != null);
  const reservoirUnits = $derived(person.devices?.reservoirUnits);
  const snapshotAt = $derived(toDate(person.devices?.snapshotAt));
  const notCurrent = $derived(readingIsNotCurrent(person));
  const hasStatusItems = $derived(showIob || showCob || reservoirUnits != null);
</script>

{#snippet statusItem(label: string, value: string, age?: string)}
  <div class="flex flex-col">
    <span class="text-xs text-muted-foreground">{label}</span>
    <span class="text-sm font-medium tabular-nums">
      {value}
      {#if age}
        <span class="text-xs font-normal text-muted-foreground">
          · {age}
        </span>
      {/if}
    </span>
  </div>
{/snippet}

{#if hasStatusItems}
  <div
    class="flex flex-wrap gap-x-6 gap-y-1 {notCurrent ? 'opacity-60' : ''}"
    data-testid="person-status-line"
  >
    {#if showIob && therapy}
      {@render statusItem("IOB", `${formatInsulinDisplay(therapy.iob)} U`)}
    {/if}
    {#if showCob && therapy}
      {@render statusItem("COB", `${Math.round(therapy.cob ?? 0)} g`)}
    {/if}
    {#if reservoirUnits != null}
      {@render statusItem(
        "Reservoir",
        `${Math.round(reservoirUnits)} U`,
        snapshotAt ? relativeAge(snapshotAt) : undefined
      )}
    {/if}
  </div>
{/if}
