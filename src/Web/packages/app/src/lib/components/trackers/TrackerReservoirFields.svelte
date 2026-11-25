<script lang="ts">
  import { NotificationUrgency } from "#lib/api/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import type { TrackerDraft } from "./draft";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();
</script>

<div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
  <div class="space-y-2">
    <Label for="lowReservoirUnits">Low reservoir alert (units)</Label>
    <Input
      id="lowReservoirUnits"
      type="number"
      min="0.5"
      max="1000"
      step="0.5"
      bind:value={draft.lowReservoirUnits}
      placeholder="e.g., 20"
    />
    <p class="text-xs text-muted-foreground">
      Alerts when the estimated reservoir level falls below this many
      units. Leave empty for no level alert.
    </p>
  </div>
  <div class="space-y-2">
    <Label for="lowReservoirUrgency">Level alert urgency</Label>
    <Select.Root type="single" bind:value={draft.lowReservoirUrgency}>
      <Select.Trigger id="lowReservoirUrgency">
        {draft.lowReservoirUrgency === NotificationUrgency.Info
          ? "Info"
          : draft.lowReservoirUrgency === NotificationUrgency.Warn
            ? "Warning"
            : draft.lowReservoirUrgency === NotificationUrgency.Hazard
              ? "Hazard"
              : "Urgent"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value={NotificationUrgency.Info} label="Info" />
        <Select.Item value={NotificationUrgency.Warn} label="Warning" />
        <Select.Item value={NotificationUrgency.Hazard} label="Hazard" />
        <Select.Item value={NotificationUrgency.Urgent} label="Urgent" />
      </Select.Content>
    </Select.Root>
  </div>
</div>
