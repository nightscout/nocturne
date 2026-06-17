<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { cn } from "$lib/utils";
  import {
    type InAppNotificationDto,
    NotificationUrgency,
  } from "$lib/api/generated/nocturne-api-client";
  import { resolveNotificationIcon } from "$lib/utils/notification-icons";
  import { resolveNotificationLabel } from "$lib/utils/notification-labels";

  interface Props {
    notification: InAppNotificationDto;
    onAction?: (actionId: string) => void;
  }

  let { notification, onAction }: Props = $props();

  // Get color classes based on urgency
  function getUrgencyClasses(urgency: NotificationUrgency | undefined): string {
    switch (urgency) {
      case NotificationUrgency.Urgent:
        return "text-red-500 bg-red-500/10 border-red-500/20";
      case NotificationUrgency.Hazard:
        return "text-orange-500 bg-orange-500/10 border-orange-500/20";
      case NotificationUrgency.Warn:
        return "text-yellow-500 bg-yellow-500/10 border-yellow-500/20";
      default:
        return "text-muted-foreground bg-muted/50 border-border";
    }
  }

  // Format relative time
  function formatRelativeTime(date: Date | undefined): string {
    if (!date) return "";
    const now = Date.now();
    const timestamp = new Date(date).getTime();
    const diffMs = now - timestamp;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return "just now";
    if (diffMins === 1) return "1m ago";
    if (diffMins < 60) return `${diffMins}m ago`;

    const diffHours = Math.floor(diffMins / 60);
    if (diffHours === 1) return "1h ago";
    if (diffHours < 24) return `${diffHours}h ago`;

    const diffDays = Math.floor(diffHours / 24);
    if (diffDays === 1) return "1d ago";
    return `${diffDays}d ago`;
  }

  // Get button variant based on action variant string
  function getButtonVariant(
    variant: string | undefined
  ): "default" | "secondary" | "outline" | "ghost" | "destructive" {
    switch (variant) {
      case "primary":
      case "default":
        return "default";
      case "secondary":
        return "secondary";
      case "destructive":
        return "destructive";
      case "ghost":
        return "ghost";
      default:
        return "outline";
    }
  }

  const Icon = $derived(resolveNotificationIcon(notification.icon, notification.category));
  const urgencyClasses = $derived(getUrgencyClasses(notification.urgency));
  const isRead = $derived(notification.readAt != null);
</script>

<div
  class={cn(
    "flex items-start gap-3 border-b p-3",
    urgencyClasses,
    isRead && "opacity-60"
  )}
>
  <div class="shrink-0 mt-0.5 relative">
    <Icon class="h-4 w-4" />
    {#if !isRead}
      <span
        class="absolute -left-1.5 top-0.5 h-2 w-2 rounded-full bg-primary"
        aria-label="Unread"
      ></span>
    {/if}
  </div>
  <div class="flex-1 min-w-0">
    <div class="flex items-center justify-between gap-2">
      <span class={cn("text-sm truncate", isRead ? "font-normal" : "font-semibold")}>
        {resolveNotificationLabel(notification.title) || "Notification"}
      </span>
      <span class="text-xs opacity-75 whitespace-nowrap">
        {formatRelativeTime(notification.createdAt)}
      </span>
    </div>
    {#if notification.subtitle}
      <p class="text-xs mt-0.5 opacity-75">
        {resolveNotificationLabel(notification.subtitle)}
      </p>
    {/if}
    {#if notification.actions && notification.actions.length > 0}
      <div class="flex items-center gap-2 mt-2 flex-wrap">
        {#each notification.actions as action (action.actionId)}
          <Button
            variant={getButtonVariant(action.variant)}
            size="sm"
            onclick={() => onAction?.(action.actionId!)}
          >
            {resolveNotificationLabel(action.label)}
          </Button>
        {/each}
      </div>
    {/if}
  </div>
</div>
