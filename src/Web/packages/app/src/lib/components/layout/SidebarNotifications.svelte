<script lang="ts">
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Bell from "@lucide/svelte/icons/bell";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import { cn } from "#lib/utils/index.js";
  import { tryGetRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { goto } from "$app/navigation";
  import { toast } from "svelte-sonner";
  import { describeSubmitError, errorStatus } from "#lib/forms/submit-error.js";
  import { resolve } from "$app/paths";
  import {
    executeAction,
    markAsRead,
    markAllAsRead,
  } from "#lib/api/generated/notifications.generated.remote.js";
  import CheckCheck from "@lucide/svelte/icons/check-check";
  import {
    NotificationUrgency,
    type InAppNotificationDto,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import NotificationItem from "./NotificationItem.svelte";
  import DndPanel from "#lib/components/alerts/DndPanel.svelte";

  // Get the realtime store for reactive notification data
  const realtimeStore = tryGetRealtimeStore();

  // State
  let isOpen = $state(false);
  const mealMatchReviewDialog = lazyComponent(
    () => import("#lib/components/meal-matching/MealMatchReviewDialog.svelte"),
  );
  let reviewNotification = $state<InAppNotificationDto | null>(null);
  const releaseNotesDialog = lazyComponent(
    () => import("#lib/components/notifications/ReleaseNotesDialog.svelte"),
  );
  let releaseNotification = $state<InAppNotificationDto | null>(null);

  // Sort notifications by urgency (Urgent > Hazard > Warn > Info), then by timestamp
  const sortedNotifications = $derived.by(() => {
    const urgencyOrder: Record<string, number> = {
      [NotificationUrgency.Urgent]: 0,
      [NotificationUrgency.Hazard]: 1,
      [NotificationUrgency.Warn]: 2,
      [NotificationUrgency.Info]: 3,
    };

    return [...realtimeStore?.inAppNotifications ?? []].sort((a, b) => {
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

  // Total active notifications drive the list / empty state; unread ones drive the
  // bell badge so the count clears when everything is marked read.
  const totalCount = $derived((realtimeStore?.inAppNotifications ?? []).length);
  const unread = $derived(
    (realtimeStore?.inAppNotifications ?? []).filter((n) => !n.readAt)
  );
  const unreadCount = $derived(unread.length);

  // Badge color reflects the highest urgency among unread notifications
  const badgeVariant = $derived<"destructive" | "warning" | "secondary">(
    unread.some((n) => n.urgency === NotificationUrgency.Urgent)
      ? "destructive"
      : unread.some((n) => n.urgency === NotificationUrgency.Hazard)
        ? "warning"
        : "secondary"
  );

  // Every action the server runs from here archives the notification, so the tray
  // drops it at once; the notificationArchived broadcast confirms it.
  async function handleAction(
    notification: InAppNotificationDto,
    actionId: string
  ) {
    isOpen = false;
    if (!notification.id) return;

    if (
      notification.type === "meal_matching.suggested_match" &&
      actionId === "review"
    ) {
      reviewNotification = notification;
      mealMatchReviewDialog.open = true;
      return;
    }

    // Compression-low review navigates to the review page rather than executing a
    // server action (which would just archive it). Mark it read on the way so the
    // badge clears; the notification auto-archives once its suggestions are resolved.
    if (
      notification.type === "glucose.compression_low_review" &&
      actionId === "review"
    ) {
      markRead(notification.id);
      goto(resolve('reports/data-quality/compression-lows'));
      return;
    }

    // The release notes open in a dialog. The notice stays until the owner dismisses it or a
    // newer one supersedes it.
    if (actionId === "release_notes") {
      markRead(notification.id);
      releaseNotification = notification;
      releaseNotesDialog.open = true;
      return;
    }

    if (notification.type === "data.rejected_uploads" && actionId === "review") {
      markRead(notification.id);
      goto(resolve('reports/data-quality/rejected-uploads'));
      return;
    }

    if (notification.type?.startsWith("system.instance_key_changed")) {
      if (actionId === "open_connectors") {
        markRead(notification.id);
        goto(resolve("settings/connectors"));
        return;
      }
      if (actionId === "open_alerts") {
        markRead(notification.id);
        goto(resolve("alerts"));
        return;
      }
      if (actionId === "open_platform_settings") {
        markRead(notification.id);
        goto(resolve("settings/admin?tab=integrations"));
        return;
      }
    }

    const removal = realtimeStore?.removeNotification(notification.id);
    try {
      await executeAction({ id: notification.id, actionId });
      removal?.settle();
    } catch (err) {
      // A 404 means the notification is already gone, so putting it back would only
      // offer the same failing action again.
      if (errorStatus(err) === 404) removal?.release();
      else removal?.restore();
      toast.error(describeSubmitError(err));
    }
  }

  async function markRead(id: string) {
    const restore = realtimeStore?.markNotificationsRead((n) => n.id === id);
    try {
      await markAsRead(id);
    } catch (err) {
      restore?.();
      toast.error(describeSubmitError(err));
    }
  }

  async function handleMarkAllRead() {
    const restore = realtimeStore?.markNotificationsRead();
    try {
      await markAllAsRead();
    } catch (err) {
      restore?.();
      toast.error(describeSubmitError(err));
    }
  }

  // Accepting or dismissing the match archives its notification on the server.
  function handleReviewComplete() {
    if (reviewNotification?.id) realtimeStore?.removeNotification(reviewNotification.id).settle();
    reviewNotification = null;
  }
</script>

<svelte:boundary>
  <Popover.Root bind:open={isOpen}>
    <Popover.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button
          {...props}
          variant="ghost"
          size="icon-sm"
          class="relative"
          aria-label="Notifications"
        >
          <Bell class="h-4 w-4" />
          {#if unreadCount > 0}
            <span
              class={cn(
                "absolute -top-1 -right-1 flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-2xs font-medium",
                badgeVariant === "destructive" && "bg-severity-urgent text-severity-urgent-foreground",
                badgeVariant === "warning" && "bg-severity-hazard text-severity-hazard-foreground",
                badgeVariant === "secondary" && "bg-severity-warn text-severity-warn-foreground"
              )}
            >
              {unreadCount}
            </span>
          {/if}
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content align="end" class="w-80 p-0">
      <div class="flex items-center justify-between border-b px-4 py-3">
        <h4 class="text-sm font-semibold">Notifications</h4>
        <div class="flex items-center gap-3">
          {#if unreadCount > 0}
            <Button
              variant="ghost-muted"
              size="xs"
              onclick={handleMarkAllRead}
            >
              <CheckCheck class="h-3.5 w-3.5" />
              Mark all read
            </Button>
          {/if}
          {#if sortedNotifications.length > 0}
            <a
              href={resolve("trackers")}
              class="text-xs text-muted-foreground hover:underline"
            >
              Manage
            </a>
          {/if}
        </div>
      </div>

      <DndPanel onNavigate={() => isOpen = false} />

      {#if totalCount === 0}
        <EmptyState
          art="chat-bubble"
          size="compact"
          title="No active notifications"
        >
          {#snippet action()}
            <a
              href={resolve("trackers")}
              class="text-xs text-primary hover:underline"
            >
              Configure trackers
            </a>
          {/snippet}
        </EmptyState>
      {:else}
        <div class="max-h-[350px] overflow-y-auto">
          {#each sortedNotifications as notification (notification.id)}
            <NotificationItem
              notification={notification}
              onAction={(actionId) => handleAction(notification, actionId)}
            />
          {/each}
        </div>
      {/if}

      <div class="border-t p-2">
        <a
          href={resolve('notifications')}
          class="flex items-center justify-between rounded-md px-2 py-1.5 text-sm hover:bg-muted"
          onclick={() => isOpen = false}
        >
          <span>View all notifications</span>
          <ChevronRight class="h-4 w-4" />
        </a>
      </div>
    </Popover.Content>
  </Popover.Root>

  {#if mealMatchReviewDialog.component}
    <mealMatchReviewDialog.component
      bind:open={mealMatchReviewDialog.open}
      onOpenChange={(value) => {
        mealMatchReviewDialog.open = value;
        if (!value) reviewNotification = null;
      }}
      notification={reviewNotification}
      onComplete={handleReviewComplete}
    />
  {/if}

  {#if releaseNotesDialog.component}
    <releaseNotesDialog.component
      bind:open={releaseNotesDialog.open}
      notification={releaseNotification}
    />
  {/if}

  {#snippet failed()}
    <Button
      variant="ghost"
      size="icon-sm"
      class="relative"
      aria-label="Notifications unavailable"
      disabled
    >
      <Bell class="h-4 w-4 text-muted-foreground" />
    </Button>
  {/snippet}
</svelte:boundary>
