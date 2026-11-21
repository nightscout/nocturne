<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import {
    formatInsulinDisplay,
    relativeAge,
    toDate,
  } from "#lib/utils/formatting.js";
  import AttentionReasons from "./AttentionReasons.svelte";
  import PersonAlerts from "./PersonAlerts.svelte";
  import PersonGlucose from "./PersonGlucose.svelte";
  import PersonLink from "./PersonLink.svelte";
  import PersonTrace from "./PersonTrace.svelte";
  import ReadingAge from "./ReadingAge.svelte";
  import { personHref, personName, readingIsNotCurrent } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
    baseDomain: string | null;
  }

  const { person, baseDomain }: Props = $props();

  const name = $derived(personName(person));
  const therapy = $derived(person.therapy);
  const showIob = $derived(!!therapy?.iobKnown && therapy.iob != null);
  const showCob = $derived(!!therapy?.cobKnown && therapy.cob != null);
  const reservoirUnits = $derived(person.devices?.reservoirUnits);
  const snapshotAt = $derived(toDate(person.devices?.snapshotAt));
  const notCurrent = $derived(readingIsNotCurrent(person));
  const hasTrace = $derived((person.recentReadings?.length ?? 0) > 0);
  const hasStatusItems = $derived(
    showIob || showCob || reservoirUnits != null
  );
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

<li>
  <PersonLink href={personHref(person, baseDomain)} class="space-y-3 px-3 py-4">
    <div class="flex items-start justify-between gap-2">
      <p class="min-w-0 truncate text-base font-semibold" title={name}>
        {name}
      </p>
      <PersonAlerts {person} />
    </div>
    <div class="flex flex-wrap items-center gap-x-4 gap-y-1">
      <PersonGlucose {person} size="lg" deltaUnit />
      <ReadingAge {person} />
    </div>
    <AttentionReasons {person} />
    {#if hasTrace}
      <div>
        <PersonTrace {person} class="h-16 w-full" />
        <div class="flex justify-between text-xs text-muted-foreground">
          <span>3 h ago</span>
          <span>Now</span>
        </div>
      </div>
    {/if}
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
  </PersonLink>
</li>
