<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Bell, ChevronRight } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import * as notificationsRemote from "$lib/data/notifications.remote";
  import {
    NotificationUrgency,
    InAppNotificationType,
    type InAppNotificationDto,
  } from "$lib/api/generated/nocturne-api-client";
  import NotificationItem from "./NotificationItem.svelte";
  import { MealMatchReviewDialog } from "$lib/components/meal-matching";

  // Get the realtime store for reactive notification data
  const realtimeStore = getRealtimeStore();

  // State
  let isOpen = $state(false);
  let reviewDialogOpen = $state(false);
  let reviewNotification = $state<InAppNotificationDto | null>(null);

  // Sort notifications by urgency (Urgent > Hazard > Warn > Info), then by timestamp
  const sortedNotifications = $derived.by(() => {
    const urgencyOrder: Record<string, number> = {
      [NotificationUrgency.Urgent]: 0,
      [NotificationUrgency.Hazard]: 1,
      [NotificationUrgency.Warn]: 2,
      [NotificationUrgency.Info]: 3,
    };

    return [...realtimeStore.inAppNotifications].sort((a, b) => {
      // First sort by urgency
      const urgencyA = urgencyOrder[a.urgency ?? NotificationUrgency.Info] ?? 3;
      const urgencyB = urgencyOrder[b.urgency ?? NotificationUrgency.Info] ?? 3;
      if (urgencyA !== urgencyB) {
        return urgencyA - urgencyB;
      }
      // Then by timestamp (newest first)
      const timeA = a.createdAt ? new Date(a.createdAt).getTime() : 0;
      const timeB = b.createdAt ? new Date(b.createdAt).getTime() : 0;
      return timeB - timeA;
    });
  });

  // Count by urgency level for badge
  const urgentCount = $derived(
    realtimeStore.inAppNotifications.filter(
      (n) => n.urgency === NotificationUrgency.Urgent
    ).length
  );
  const hazardCount = $derived(
    realtimeStore.inAppNotifications.filter(
      (n) => n.urgency === NotificationUrgency.Hazard
    ).length
  );

  // Badge count is total notifications
  const badgeCount = $derived(realtimeStore.inAppNotifications.length);

  // Badge color based on highest urgency
  const badgeVariant = $derived<"destructive" | "warning" | "secondary">(
    urgentCount > 0
      ? "destructive"
      : hazardCount > 0
        ? "warning"
        : "secondary"
  );

  // Handle action on a notification
  async function handleAction(
    notification: InAppNotificationDto,
    actionId: string
  ) {
    isOpen = false;

    // Handle review action for meal match notifications
    if (
      notification.type === InAppNotificationType.SuggestedMealMatch &&
      actionId === "review"
    ) {
      reviewNotification = notification;
      reviewDialogOpen = true;
      return;
    }

    try {
      await notificationsRemote.executeAction({
        id: notification.id!,
        actionId,
      });
    } catch (err) {
      console.error("Failed to execute notification action:", err);
    }
  }

  function handleReviewComplete() {
    reviewNotification = null;
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
      <h4 class="text-sm font-semibold">Notifications</h4>
      {#if sortedNotifications.length > 0}
        <a
          href="/settings/trackers"
          class="text-xs text-muted-foreground hover:underline"
        >
          Manage
        </a>
      {/if}
    </div>

    {#if badgeCount === 0}
      <div class="flex flex-col items-center justify-center py-8 text-center">
        <Bell class="h-8 w-8 text-muted-foreground/50 mb-2" />
        <p class="text-sm text-muted-foreground">No active notifications</p>
        <a
          href="/settings/trackers"
          class="mt-2 text-xs text-primary hover:underline"
        >
          Set up trackers
        </a>
      </div>
    {:else}
      <div class="max-h-[350px] overflow-y-auto">
        {#each sortedNotifications as notification (notification.id)}
          <NotificationItem
            {notification}
            onAction={(actionId) => handleAction(notification, actionId)}
          />
        {/each}
      </div>
    {/if}

    <div class="border-t p-2">
      <a
        href="/notifications"
        class="flex items-center justify-between rounded-md px-2 py-1.5 text-sm hover:bg-muted"
        onclick={() => (isOpen = false)}
      >
        <span>View all notifications</span>
        <ChevronRight class="h-4 w-4" />
      </a>
    </div>
  </Popover.Content>
</Popover.Root>

<MealMatchReviewDialog
  bind:open={reviewDialogOpen}
  onOpenChange={(value) => {
    reviewDialogOpen = value;
    if (!value) reviewNotification = null;
  }}
  notification={reviewNotification}
  onComplete={handleReviewComplete}
/>
