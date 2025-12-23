<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import {
    Bell,
    Clock,
    Check,
    Timer,
    AlertTriangle,
    AlertCircle,
    Info,
    ChevronRight,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import * as trackersRemote from "$lib/data/trackers.remote";
  import type { TrackerInstanceDto } from "$lib/api/generated/nocturne-api-client";

  // Get the realtime store for reactive tracker data
  const realtimeStore = getRealtimeStore();

  // State
  let isOpen = $state(false);

  // Use tracker notifications from realtime store
  const notifications = $derived(realtimeStore.trackerNotifications);

  // Get auth store
  const authStore = getAuthStore();

  // Count by level for badge
  const urgentCount = $derived(
    notifications.filter((n) => n.level === "urgent").length
  );
  const hazardCount = $derived(
    notifications.filter((n) => n.level === "hazard").length
  );

  // Badge color based on highest severity
  const badgeCount = $derived(notifications.length);
  const badgeVariant = $derived<"destructive" | "warning" | "secondary">(
    urgentCount > 0 ? "destructive" : hazardCount > 0 ? "warning" : "secondary"
  );

  // Check if user can globally acknowledge
  const canGlobalAck = $derived(
    authStore.hasPermission("api:notifications:ack")
  );

  // Format age for display
  function formatAge(hours: number): string {
    if (hours < 1) {
      return `${Math.floor(hours * 60)}m`;
    }
    if (hours < 24) {
      return `${Math.floor(hours)}h`;
    }
    const days = Math.floor(hours / 24);
    const remainingHours = Math.floor(hours % 24);
    return remainingHours > 0 ? `${days}d ${remainingHours}h` : `${days}d`;
  }

  // Build notification message
  function buildMessage(
    instance: TrackerInstanceDto & { level: string }
  ): string {
    const def = realtimeStore.trackerDefinitions.find(
      (d) => d.id === instance.definitionId
    );
    if (!def) return "Tracker active";
    switch (instance.level) {
      case "urgent":
        return `Exceeded ${def.urgentHours}h - change urgently!`;
      case "hazard":
        return `Exceeded ${def.hazardHours}h - change soon`;
      case "warn":
        return `Approaching ${def.lifespanHours ?? def.warnHours}h limit`;
      default:
        return `Active for ${Math.floor(instance.ageHours ?? 0)}h`;
    }
  }

  // Get level icon
  function getLevelIcon(level: string) {
    switch (level) {
      case "urgent":
        return AlertCircle;
      case "hazard":
        return AlertTriangle;
      case "warn":
        return Timer;
      default:
        return Info;
    }
  }

  // Get level colors
  function getLevelClass(level: string): string {
    switch (level) {
      case "urgent":
        return "text-red-500 bg-red-500/10 border-red-500/20";
      case "hazard":
        return "text-orange-500 bg-orange-500/10 border-orange-500/20";
      case "warn":
        return "text-yellow-500 bg-yellow-500/10 border-yellow-500/20";
      default:
        return "text-muted-foreground bg-muted border-border";
    }
  }

  // Handlers
  async function handleSnooze(id: string, mins: number) {
    try {
      await trackersRemote.ackInstance({ id, request: { snoozeMins: mins } });
    } catch (err) {
      console.error("Failed to snooze:", err);
    }
  }

  async function handleComplete(id: string) {
    try {
      await trackersRemote.completeInstance({
        id,
        request: { reason: 0 }, // Completed
      });
    } catch (err) {
      console.error("Failed to complete:", err);
    }
  }

  async function handleGlobalAck(id: string) {
    try {
      await trackersRemote.ackInstance({
        id,
        request: { snoozeMins: 30, global: true },
      });
    } catch (err) {
      console.error("Failed to global ack:", err);
    }
  }
</script>

<Popover.Root bind:open={isOpen}>
  <Popover.Trigger>
    {#snippet child({ props })}
      <Button
        {...props}
        variant="ghost"
        size="icon"
        class="relative h-8 w-8"
        aria-label="Notifications"
      >
        <Bell class="h-4 w-4" />
        {#if badgeCount > 0}
          <span
            class={cn(
              "absolute -top-1 -right-1 flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-medium",
              badgeVariant === "destructive" && "bg-red-500 text-white",
              badgeVariant === "warning" && "bg-orange-500 text-white",
              badgeVariant === "secondary" && "bg-yellow-500 text-black"
            )}
          >
            {badgeCount}
          </span>
        {/if}
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content align="end" class="w-80 p-0">
    <div class="flex items-center justify-between border-b px-4 py-3">
      <h4 class="text-sm font-semibold">Tracker Notifications</h4>
      {#if notifications.length > 0}
        <a
          href="/settings/trackers"
          class="text-xs text-muted-foreground hover:underline"
        >
          Manage
        </a>
      {/if}
    </div>

    {#if notifications.length === 0}
      <div class="flex flex-col items-center justify-center py-8 text-center">
        <Bell class="h-8 w-8 text-muted-foreground/50 mb-2" />
        <p class="text-sm text-muted-foreground">No active notifications</p>
        <a
          href="/settings/trackers"
          class="mt-2 text-xs text-primary hover:underline"
        >
          Set up trackers →
        </a>
      </div>
    {:else}
      <div class="max-h-[300px] overflow-y-auto">
        {#each notifications as notification (notification.id)}
          {@const LevelIcon = getLevelIcon(notification.level)}
          <div
            class={cn(
              "flex items-start gap-3 border-b p-3 last:border-b-0",
              getLevelClass(notification.level)
            )}
          >
            <div class="flex-shrink-0 mt-0.5">
              <LevelIcon class="h-4 w-4" />
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center justify-between gap-2">
                <span class="text-sm font-medium truncate">
                  {notification.definitionName}
                </span>
                <span class="text-xs opacity-75 whitespace-nowrap">
                  {formatAge(notification.ageHours ?? 0)}
                </span>
              </div>
              <p class="text-xs mt-0.5 opacity-75">
                {buildMessage(notification)}
              </p>
              <div class="flex items-center gap-2 mt-2">
                <Button
                  variant="outline"
                  size="sm"
                  class="h-6 text-xs px-2"
                  onclick={() => handleSnooze(notification.id!, 30)}
                >
                  <Clock class="h-3 w-3 mr-1" />
                  Snooze
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  class="h-6 text-xs px-2"
                  onclick={() => handleComplete(notification.id!)}
                >
                  <Check class="h-3 w-3 mr-1" />
                  Done
                </Button>
                {#if canGlobalAck}
                  <Button
                    variant="ghost"
                    size="sm"
                    class="h-6 text-xs px-2"
                    onclick={() => handleGlobalAck(notification.id!)}
                    title="Acknowledge for all devices"
                  >
                    Ack All
                  </Button>
                {/if}
              </div>
            </div>
          </div>
        {/each}
      </div>
    {/if}

    <div class="border-t p-2">
      <a
        href="/settings/trackers"
        class="flex items-center justify-between rounded-md px-2 py-1.5 text-sm hover:bg-muted"
        onclick={() => (isOpen = false)}
      >
        <span>View all trackers</span>
        <ChevronRight class="h-4 w-4" />
      </a>
    </div>
  </Popover.Content>
</Popover.Root>
