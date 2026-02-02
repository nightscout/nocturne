<script lang="ts" module>
  import { NotificationUrgency } from "$api";

  export interface TrackerNotification {
    id?: string;
    urgency: NotificationUrgency;
    hours: number | undefined;
    description?: string;
    displayOrder?: number;
  }
</script>

<script lang="ts">
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { ThresholdEditor } from "$lib/components/ui/threshold-editor";
  import { cn } from "$lib/utils";

  interface Props {
    /** The notifications array (bindable) */
    notifications?: TrackerNotification[];
    /** Additional CSS classes */
    class?: string;
    /** Tracker mode for display formatting */
    mode?: "Duration" | "Event";
    /** Lifespan hours for negative threshold validation (Duration mode only) */
    lifespanHours?: number | undefined;
  }

  let {
    notifications = $bindable([]),
    class: className,
    mode = "Duration",
    lifespanHours,
  }: Props = $props();

  const urgencyOptions: {
    value: NotificationUrgency;
    label: string;
    color: string;
  }[] = [
    { value: NotificationUrgency.Info, label: "Info", color: "text-blue-500" },
    {
      value: NotificationUrgency.Warn,
      label: "Warning",
      color: "text-yellow-500",
    },
    {
      value: NotificationUrgency.Hazard,
      label: "Hazard",
      color: "text-orange-500",
    },
    {
      value: NotificationUrgency.Urgent,
      label: "Urgent",
      color: "text-red-500",
    },
  ];

  function getUrgencyConfig(urgency: NotificationUrgency) {
    return urgencyOptions.find((o) => o.value === urgency) ?? urgencyOptions[0];
  }

  function createThreshold(): TrackerNotification {
    return {
      urgency: NotificationUrgency.Info,
      hours: undefined,
      description: "",
    };
  }
</script>

<ThresholdEditor
  bind:thresholds={notifications}
  label="Notification Thresholds"
  {mode}
  {lifespanHours}
  {createThreshold}
  class={className}
  emptyDescription="Add thresholds to get notified as the tracker ages"
>
  {#snippet extraColumns({ threshold, update })}
    {@const config = getUrgencyConfig(threshold.urgency)}
    <div class="shrink-0 w-28">
      <Label class="text-xs text-muted-foreground mb-1 block">Level</Label>
      <Select.Root
        type="single"
        value={threshold.urgency}
        onValueChange={(v) => update("urgency", v)}
      >
        <Select.Trigger class="w-full">
          <span class={config.color}>{config.label}</span>
        </Select.Trigger>
        <Select.Content>
          {#each urgencyOptions as option}
            <Select.Item value={option.value}>
              <span class={option.color}>{option.label}</span>
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  {/snippet}
</ThresholdEditor>
