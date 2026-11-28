<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { relativeAge, toDate } from "#lib/utils/formatting.js";
  import Battery from "@lucide/svelte/icons/battery";
  import PersonAlerts from "./PersonAlerts.svelte";
  import PersonCell from "./PersonCell.svelte";
  import PersonGlucose from "./PersonGlucose.svelte";
  import PersonSelect from "./PersonSelect.svelte";
  import PersonTrace from "./PersonTrace.svelte";
  import ReadingAge from "./ReadingAge.svelte";
  import { ROW_GRID, ROW_ITEM } from "./row-grid.js";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const reservoirUnits = $derived(person.devices?.reservoirUnits);
  const batteryPercent = $derived(person.devices?.pumpBatteryPercent);
  const snapshotAt = $derived(toDate(person.devices?.snapshotAt));
</script>

<li class={ROW_ITEM}>
  <PersonSelect
    {person}
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
  </PersonSelect>
</li>
