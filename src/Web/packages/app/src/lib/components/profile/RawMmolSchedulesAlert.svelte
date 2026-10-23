<script lang="ts">
  import { page } from "$app/state";
  import * as Alert from "$lib/components/ui/alert";
  import { Button } from "$lib/components/ui/button";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import Pencil from "@lucide/svelte/icons/pencil";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import {
    dismissRawMmolIsf,
    getRawMmolSchedules,
  } from "$api/generated/profiles.generated.remote";
  import { RawMmolScheduleKind, type RawMmolSchedule } from "$api-clients";
  import RawMmolScheduleEditDialog from "./RawMmolScheduleEditDialog.svelte";

  const canWrite = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "therapy.readwrite")
  );

  const schedulesQuery = getRawMmolSchedules(undefined);
  const schedules = $derived(schedulesQuery.current ?? []);

  let editing = $state<RawMmolSchedule | null>(null);
  let editOpen = $state(false);
  let dismissing = $state<string | null>(null);
  let dismissError = $state<string | null>(null);

  function edit(item: RawMmolSchedule) {
    editing = item;
    editOpen = true;
  }

  async function dismiss(id: string) {
    dismissing = id;
    dismissError = null;
    try {
      await dismissRawMmolIsf(id);
    } catch (err) {
      dismissError = describeSubmitError(err, "Failed to save your answer");
    } finally {
      dismissing = null;
    }
  }

  function storedValues(item: RawMmolSchedule): string {
    return (item.entries ?? [])
      .map((e) =>
        item.kind === RawMmolScheduleKind.TargetRange
          ? `${e.time ?? "?"} ${e.low ?? "?"}-${e.high ?? "?"}`
          : `${e.time ?? "?"} ${e.value ?? "?"}`
      )
      .join(", ");
  }
</script>

{#if canWrite && schedules.length > 0}
  <Alert.Root variant="warning">
    <TriangleAlert class="h-4 w-4" />
    <Alert.Title>Some profile values look like mmol/L</Alert.Title>
    <Alert.Description>
      <p>
        These values look like mmol/L values stored as mg/dL. Nocturne uses them as mg/dL, so
        they change insulin and alert calculations. Check each schedule and correct its values.
      </p>
      <ul class="mt-3 space-y-2">
        {#each schedules as item (item.id)}
          <li class="flex flex-wrap items-center gap-2">
            <span class="font-medium">{item.profileName}</span>
            <span>
              {item.kind === RawMmolScheduleKind.TargetRange
                ? "Target range"
                : "Insulin sensitivity (ISF)"}:
            </span>
            <span class="text-muted-foreground tabular-nums">{storedValues(item)}</span>
            <Button variant="outline" size="sm" onclick={() => edit(item)}>
              <Pencil class="mr-1 h-4 w-4" />
              Edit values
            </Button>
            {#if item.canDismiss && item.id}
              {@const id = item.id}
              <Button
                variant="ghost"
                size="sm"
                disabled={dismissing !== null}
                onclick={() => dismiss(id)}
              >
                {dismissing === id ? "Saving..." : "These values are already mg/dL"}
              </Button>
            {/if}
          </li>
        {/each}
      </ul>
      {#if dismissError}
        <p class="mt-2 text-sm text-destructive">{dismissError}</p>
      {/if}
    </Alert.Description>
  </Alert.Root>

  <RawMmolScheduleEditDialog item={editing} bind:open={editOpen} />
{/if}
