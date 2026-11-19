<script lang="ts">
  import { TrackerMode } from "#lib/api/index.js";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import { cn } from "#lib/utils/index.js";
  import { NotificationUrgency } from "#lib/api/index.js";
  import TrackerThresholdRow from "./TrackerThresholdRow.svelte";
  import type { TrackerNotification } from "./types";

  interface Props {
    /** The notifications array (bindable) */
    notifications?: TrackerNotification[];
    /** Additional CSS classes */
    class?: string;
    /** Whether a time counts from a start (Duration) or from a date (Event). */
    mode?: TrackerMode;
    /** The lifespan a time before the end counts back from (Duration mode only). */
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

  // A row keeps what the person typed, and nothing but its place identifies it. A removal moves
  // the rows after it, so it starts every row again from the stored values.
  let removals = $state(0);

  function addNotification() {
    // Several notifications at one urgency are allowed, so a new one always starts at Info.
    notifications = [
      ...notifications,
      { urgency: NotificationUrgency.Info, hours: undefined, description: "" },
    ];
  }

  function removeNotification(index: number) {
    notifications = notifications.filter((_, i) => i !== index);
    removals++;
  }

  function updateNotification(index: number, patch: Partial<TrackerNotification>) {
    notifications = notifications.map((n, i) => (i === index ? { ...n, ...patch } : n));
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
      {#each notifications as notification, i (`${removals}:${i}`)}
        <TrackerThresholdRow
          {notification}
          index={i}
          {mode}
          {lifespanHours}
          {canOpenRule}
          onChange={(patch) => updateNotification(i, patch)}
          onRemove={() => removeNotification(i)}
        />
      {/each}
    </div>
  {/if}

  <p class="text-xs text-muted-foreground">
    Each threshold is delivered through a managed alert rule. Use Channels to
    open that rule. A new threshold gets its rule after you save the tracker.
  </p>
</div>
