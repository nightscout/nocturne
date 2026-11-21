<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import { relativeAge, toDate } from "#lib/utils/formatting.js";
  import Battery from "@lucide/svelte/icons/battery";
  import PersonAlerts from "./PersonAlerts.svelte";
  import PersonCell from "./PersonCell.svelte";
  import PersonGlucose from "./PersonGlucose.svelte";
  import PersonLink from "./PersonLink.svelte";
  import PersonTrace from "./PersonTrace.svelte";
  import ReadingAge from "./ReadingAge.svelte";
  import { personHref } from "./person.js";
  import { ROW_GRID } from "./row-grid.js";

  interface Props {
    person: TenantOverviewItem;
    baseDomain: string | null;
  }

  const { person, baseDomain }: Props = $props();

  const reservoirUnits = $derived(person.devices?.reservoirUnits);
  const batteryPercent = $derived(person.devices?.pumpBatteryPercent);
  const snapshotAt = $derived(toDate(person.devices?.snapshotAt));
</script>

<li>
  <PersonLink
    href={personHref(person, baseDomain)}
    class="flex flex-wrap items-center gap-x-3 gap-y-1 {ROW_GRID}"
  >
    <PersonCell {person} class="basis-full @2xl:basis-auto" />
    <PersonGlucose {person} size="xs" />
    <ReadingAge {person} />
    <div class="hidden @4xl:block">
      <PersonTrace {person} class="h-8 w-full" />
    </div>
    <div class="ml-auto flex justify-end @2xl:ml-0">
      <PersonAlerts {person} />
    </div>
    <div
      class="hidden flex-col text-sm tabular-nums @2xl:flex"
      data-testid="pump-cell"
    >
      {#if reservoirUnits != null || batteryPercent != null}
        <span>
          {#if reservoirUnits != null}{Math.round(reservoirUnits)} U{/if}
          {#if reservoirUnits != null && batteryPercent != null}
            ·
          {/if}
          {#if batteryPercent != null}
            <span class="inline-flex items-center gap-0.5">
              <Battery class="size-3.5" aria-hidden="true" />
              <span class="sr-only">Pump battery</span>{batteryPercent}%
            </span>
          {/if}
        </span>
        {#if snapshotAt}
          <span class="text-xs text-muted-foreground" data-testid="pump-age">
            {relativeAge(snapshotAt)}
          </span>
        {/if}
      {/if}
    </div>
  </PersonLink>
</li>
