<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { DurationInput } from "$lib/components/ui/duration-input";
  import { Plus, Trash2 } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { NotificationUrgency } from "$api";

  export interface TrackerNotification {
    id?: string; // For existing thresholds (from API)
    urgency: NotificationUrgency;
    hours: number | undefined;
    description?: string;
    displayOrder?: number;
  }

  interface Props {
    /** The notifications array (bindable) */
    notifications?: TrackerNotification[];
    /** Additional CSS classes */
    class?: string;
  }

  let { notifications = $bindable([]), class: className }: Props = $props();

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

  function addNotification() {
    // Always default to Info - users can change urgency as needed
    // Multiple notifications at the same urgency level are allowed
    notifications = [
      ...notifications,
      { urgency: NotificationUrgency.Info, hours: undefined, description: "" },
    ];
  }

  function removeNotification(index: number) {
    notifications = notifications.filter((_, i) => i !== index);
  }

  function updateNotification(
    index: number,
    field: keyof TrackerNotification,
    value: any
  ) {
    notifications = notifications.map((n, i) =>
      i === index ? { ...n, [field]: value } : n
    );
  }
</script>

<div class={cn("space-y-3", className)}>
  <div class="flex items-center justify-between">
    <Label class="text-sm font-medium">Notification Thresholds</Label>
    <Button
      variant="outline"
      size="sm"
      type="button"
      onclick={addNotification}
      disabled={notifications.length >= 4}
    >
      <Plus class="h-4 w-4 mr-1" />
      Add
    </Button>
  </div>

  {#if notifications.length === 0}
    <div
      class="text-center py-4 text-muted-foreground text-sm border border-dashed rounded-lg"
    >
      <p>No notification thresholds configured</p>
      <p class="text-xs mt-1">
        Add thresholds to get notified as the tracker ages
      </p>
    </div>
  {:else}
    <div class="space-y-3">
      {#each notifications as notification, i}
        {@const config = getUrgencyConfig(notification.urgency)}
        <div class="flex gap-2 items-start p-3 border rounded-lg bg-muted/30">
          <div class="flex-shrink-0 w-28">
            <Label class="text-xs text-muted-foreground mb-1 block">
              Level
            </Label>
            <Select.Root
              type="single"
              value={notification.urgency}
              onValueChange={(v) => updateNotification(i, "urgency", v)}
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

          <div class="flex-shrink-0 w-36">
            <Label class="text-xs text-muted-foreground mb-1 block">
              After (hours)
            </Label>
            <DurationInput
              value={notification.hours}
              onchange={(v) => updateNotification(i, "hours", v)}
              placeholder="e.g., 7x24"
            />
          </div>

          <div class="flex-1 min-w-0">
            <Label class="text-xs text-muted-foreground mb-1 block">
              Description (optional)
            </Label>
            <Input
              value={notification.description ?? ""}
              oninput={(e) =>
                updateNotification(i, "description", e.currentTarget.value)}
              placeholder="Message shown when triggered"
            />
          </div>

          <div class="flex-shrink-0 pt-5">
            <Button
              variant="ghost"
              size="icon"
              type="button"
              class="h-9 w-9 text-muted-foreground hover:text-destructive"
              onclick={() => removeNotification(i)}
            >
              <Trash2 class="h-4 w-4" />
              <span class="sr-only">Remove notification</span>
            </Button>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  {#if notifications.length > 0}
    <p class="text-xs text-muted-foreground">
      Notifications trigger when tracker age exceeds the specified hours.
    </p>
  {/if}
</div>
