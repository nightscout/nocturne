<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import Target from "@lucide/svelte/icons/target";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import GlucoseScheduleEditor from "$lib/components/schedule/GlucoseScheduleEditor.svelte";
  import {
    getSensitivityScheduleById,
    getTargetRangeScheduleById,
    updateSensitivitySchedule,
    updateTargetRangeSchedule,
  } from "$api/generated/profiles.generated.remote";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { RawMmolScheduleKind, type RawMmolSchedule } from "$api-clients";

  interface Props {
    /** The listed schedule to edit; the dialog shows nothing without one */
    item: RawMmolSchedule | null;
    open?: boolean;
  }

  let { item, open = $bindable(false) }: Props = $props();

  const targetQuery = $derived(
    item?.id && item.kind === RawMmolScheduleKind.TargetRange
      ? getTargetRangeScheduleById(item.id)
      : null
  );
  const isfQuery = $derived(
    item?.id && item.kind === RawMmolScheduleKind.Sensitivity
      ? getSensitivityScheduleById(item.id)
      : null
  );
  const loadError = $derived(targetQuery?.error ?? isfQuery?.error);
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-2xl">
    <Dialog.Header>
      <Dialog.Title>Correct the values of {item?.profileName}</Dialog.Title>
      <Dialog.Description>
        The values below are the stored values. They look like mmol/L values stored as mg/dL,
        about 18 times too small. Enter the correct mg/dL values. Nocturne does not change the values for you.
      </Dialog.Description>
    </Dialog.Header>

    {#if loadError}
      <p class="text-sm text-destructive">
        {remoteErrorMessage(loadError, "Failed to load the schedule")}
      </p>
    {:else if item?.id && targetQuery?.current}
      {@const schedule = targetQuery.current}
      {@const id = item.id}
      <GlucoseScheduleEditor
        title="Target Range"
        description="Desired blood glucose range"
        unit="mg/dL"
        icon={Target}
        iconClass="text-glucose-in-range"
        range
        raw
        entries={schedule.entries ?? []}
        save={async (entries) => {
          await updateTargetRangeSchedule({ id, request: { ...schedule, entries } });
          open = false;
        }}
        cancel={() => (open = false)}
        saveErrorMessage="Failed to save target range"
      />
    {:else if item?.id && isfQuery?.current}
      {@const schedule = isfQuery.current}
      {@const id = item.id}
      <GlucoseScheduleEditor
        title="Insulin Sensitivity (ISF)"
        description="BG drop per unit of insulin"
        unit="mg/dL/U"
        icon={TrendingUp}
        range={false}
        raw
        entries={schedule.entries ?? []}
        save={async (entries) => {
          await updateSensitivitySchedule({ id, request: { ...schedule, entries } });
          open = false;
        }}
        cancel={() => (open = false)}
        saveErrorMessage="Failed to save insulin sensitivity"
      />
    {:else}
      <p class="text-sm text-muted-foreground">Loading...</p>
    {/if}
  </Dialog.Content>
</Dialog.Root>
