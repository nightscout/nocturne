<script lang="ts">
  import * as trackersRemote from "#lib/api/generated/trackers.generated.remote.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { getNightscoutEventTypeLabel } from "#lib/constants/device-event-types.svelte.js";
  import type { TrackerDraft } from "./draft";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();

  const none = "none";
  const deviceEventTypesQuery = trackersRemote.getDeviceEventTypes();
  const deviceEventTypes = $derived(deviceEventTypesQuery.current ?? []);
</script>

{#snippet picker(
  id: string,
  value: string | undefined,
  onSelect: (type: string | undefined) => void,
)}
  <Select.Root
    type="single"
    value={value ?? none}
    onValueChange={(selected) => onSelect(selected === none ? undefined : selected)}
  >
    <Select.Trigger {id} data-testid={id}>
      {value ? getNightscoutEventTypeLabel(value) : "None"}
    </Select.Trigger>
    <Select.Content>
      <Select.Item value={none} label="None" />
      {#each deviceEventTypes as eventType (eventType)}
        <Select.Item value={eventType} label={getNightscoutEventTypeLabel(eventType)} />
      {/each}
    </Select.Content>
  </Select.Root>
{/snippet}

<div class="space-y-3">
  <Label>Event integration (Nightscout)</Label>
  <p class="text-xs text-muted-foreground -mt-1">
    Optionally create treatment events when this tracker starts or completes.
    This maintains compatibility with existing CAGE/SAGE pills.
  </p>

  <div class="space-y-2">
    <Label for="startEventType" size="sm">Create event on start</Label>
    {@render picker("startEventType", draft.startEventType, (type) => (draft.startEventType = type))}
  </div>

  <div class="space-y-2">
    <Label for="completionEventType" size="sm">Create event on completion</Label>
    {@render picker(
      "completionEventType",
      draft.completionEventType,
      (type) => (draft.completionEventType = type),
    )}
  </div>
</div>
