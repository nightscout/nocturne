<script lang="ts">
  import { TrackerMode } from "#lib/api/index.js";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { enumValue } from "#lib/components/ui/enum-value.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { DurationInput } from "#lib/components/ui/duration-input/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Bell from "@lucide/svelte/icons/bell";
  import { cn } from "#lib/utils/index.js";
  import { NotificationUrgency } from "#lib/api/index.js";
  import type { TrackerNotification } from "./types";

  interface Props {
    /** The notifications array (bindable) */
    notifications?: TrackerNotification[];
    /** Additional CSS classes */
    class?: string;
    /** Tracker mode for display formatting */
    mode?: TrackerMode;
    /** Lifespan hours for negative threshold validation (Duration mode only) */
    lifespanHours?: number | undefined;
  }

  let {
    notifications = $bindable([]),
    class: className,
    mode = TrackerMode.Duration,
    lifespanHours,
  }: Props = $props();

  const canOpenRule = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.read")
  );

  const urgencyOptions: {
    value: NotificationUrgency;
    label: string;
    color: string;
  }[] = [
    { value: NotificationUrgency.Info, label: "Info", color: "text-severity-info" },
    {
      value: NotificationUrgency.Warn,
      label: "Warning",
      color: "text-severity-warn",
    },
    {
      value: NotificationUrgency.Hazard,
      label: "Hazard",
      color: "text-severity-hazard",
    },
    {
      value: NotificationUrgency.Urgent,
      label: "Urgent",
      color: "text-severity-urgent",
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

  function updateNotification<K extends keyof TrackerNotification>(
    index: number,
    field: K,
    value: TrackerNotification[K]
  ) {
    notifications = notifications.map((n, i) =>
      i === index ? { ...n, [field]: value } : n
    );
  }
</script>

<div class={cn("space-y-3", className)} data-testid="tracker-thresholds">
  <div class="flex items-center justify-between">
    <Label>Notification thresholds</Label>
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
    <EmptyState
      art="stopwatch"
      size="compact"
      variant="outline"
      title="No notification thresholds configured"
      body="Add thresholds to get notified as the tracker ages"
    />
  {:else}
    <div class="space-y-3">
      <!-- eslint-disable-next-line svelte/require-each-key -- each notification is replaced on every edit and new ones have no id, so neither the object nor an id identifies a row -->
      {#each notifications as notification, i}
        {@const config = getUrgencyConfig(notification.urgency)}
        <div class="flex flex-wrap gap-2 items-start p-3 border rounded-lg bg-muted/30">
          <div class="flex-shrink-0 w-28">
            <Label size="sm" variant="muted" class="mb-1 block">
              Level
            </Label>
            <Select.Root
              type="single"
              value={notification.urgency}
              onValueChange={(v) => {
                const urgency = enumValue(NotificationUrgency, v);
                if (urgency) updateNotification(i, "urgency", urgency);
              }}
            >
              <Select.Trigger class="w-full">
                <span class={config.color}>{config.label}</span>
              </Select.Trigger>
              <Select.Content>
                {#each urgencyOptions as option (option.value)}
                  <Select.Item value={option.value}>
                    <span class={option.color}>{option.label}</span>
                  </Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="flex-shrink-0 w-36">
            <Label size="sm" variant="muted" class="mb-1 block">
              {mode === TrackerMode.Event ? "Hours" : "After (hours)"}
            </Label>
            <DurationInput
              value={notification.hours}
              onchange={(v) => updateNotification(i, "hours", v)}
              placeholder="e.g., 7x24 or -24"
              {mode}
              {lifespanHours}
            />
          </div>

          <div class="order-last basis-full min-w-0">
            <Label size="sm" variant="muted" class="mb-1 block">
              Description (optional)
            </Label>
            <Input
              value={notification.description ?? ""}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
                updateNotification(i, "description", e.currentTarget.value)}
              placeholder="Message shown when triggered"
            />
          </div>

          <div class="ml-auto flex-shrink-0 pt-5 flex items-center gap-1">
            <!-- New tab: this editor lives in a dialog, so in-place navigation
                 would discard the tracker edits made so far. -->
            {#if notification.alertRuleId && canOpenRule}
              <Button
                variant="ghost-muted"
                href="/alerts/{notification.alertRuleId}"
                target="_blank"
                rel="noopener"
                title="Configure delivery channels for this threshold"
              >
                <Bell class="h-4 w-4" />
                Channels
              </Button>
            {/if}
            <Button
              variant="ghost-destructive"
              size="icon"
              type="button"
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
      {#if mode === TrackerMode.Event}
        Negative = before event, positive = after event.
      {:else}
        Positive = after start, negative = before expiration.
      {/if}
    </p>
  {/if}

  <p class="text-xs text-muted-foreground">
    Each threshold is delivered through a managed alert rule. Use Channels to
    open that rule. A new threshold gets its rule after you save the tracker.
  </p>
</div>
