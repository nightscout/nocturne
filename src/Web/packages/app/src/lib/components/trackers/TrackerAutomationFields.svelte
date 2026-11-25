<script lang="ts">
  import * as trackersRemote from "#lib/api/generated/trackers.generated.remote.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { getNightscoutEventTypeLabel } from "#lib/constants/device-event-types.svelte.js";
  import { followsEvents, type TrackerDraft } from "./draft";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();

  const triggerEventTypesQuery = trackersRemote.getTriggerEventTypes();
  const triggerEventTypes = $derived(triggerEventTypesQuery.current ?? []);

  const endEventTypesQuery = trackersRemote.getEndEventTypes();
  const endEventTypes = $derived(endEventTypesQuery.current ?? []);

  const toggled = (list: string[], eventType: string, checked: boolean) =>
    checked ? [...list, eventType] : list.filter((t) => t !== eventType);
</script>

<div class="space-y-3" data-testid="tracker-triggers">
  <div>
    <Label>Restart automatically on</Label>
    <p class="text-xs text-muted-foreground">
      When your pump, loop or a connector reports one of these, the running
      tracker is completed and a new one starts at that moment.
    </p>
  </div>
  <div class="grid grid-cols-1 gap-2 @sm:grid-cols-2">
    {#each triggerEventTypes as eventType (eventType)}
      <div class="flex items-center gap-2">
        <Checkbox
          id="trigger-{eventType}"
          checked={draft.triggerEventTypes.includes(eventType)}
          onCheckedChange={(checked) =>
            (draft.triggerEventTypes = toggled(draft.triggerEventTypes, eventType, checked === true))}
        />
        <Label for="trigger-{eventType}" variant="option" class="cursor-pointer">
          {getNightscoutEventTypeLabel(eventType)}
        </Label>
      </div>
    {/each}
  </div>
</div>
<div class="space-y-3" data-testid="tracker-ends">
  <div>
    <Label>Stop automatically on</Label>
    <p class="text-xs text-muted-foreground">
      When your pump, loop or a connector reports one of these, the running
      tracker is completed. A new one does not start until a restart event.
    </p>
  </div>
  <div class="grid grid-cols-2 gap-2">
    {#each endEventTypes as eventType (eventType)}
      <div class="flex items-center gap-2">
        <Checkbox
          id="end-{eventType}"
          checked={draft.endEventTypes.includes(eventType)}
          onCheckedChange={(checked) =>
            (draft.endEventTypes = toggled(draft.endEventTypes, eventType, checked === true))}
        />
        <Label for="end-{eventType}" variant="option" class="cursor-pointer">
          {getNightscoutEventTypeLabel(eventType)}
        </Label>
      </div>
    {/each}
  </div>
  {#if followsEvents(draft)}
    <div class="space-y-2">
      <Label for="triggerNotesContains" size="sm">
        Only when the event's notes contain (optional)
      </Label>
      <Input
        id="triggerNotesContains"
        bind:value={draft.triggerNotesContains}
        placeholder="e.g., left"
      />
      <p class="text-xs text-muted-foreground">
        Use this to keep two trackers that listen for the same event apart,
        such as a left and a right site.
      </p>
    </div>
  {/if}
</div>
