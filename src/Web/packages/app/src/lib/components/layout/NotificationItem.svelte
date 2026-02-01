<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Info,
    Bell,
    Timer,
    KeyRound,
    Settings2,
    Utensils,
    TrendingDown,
    HelpCircle,
    User,
    RefreshCw,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import {
    type InAppNotificationDto,
    InAppNotificationType,
    NotificationUrgency,
  } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    notification: InAppNotificationDto;
    onAction?: (actionId: string) => void;
  }

  let { notification, onAction }: Props = $props();

  // Get icon based on notification type
  function getIcon(type: InAppNotificationType | undefined) {
    switch (type) {
      case InAppNotificationType.PasswordResetRequest:
        return KeyRound;
      case InAppNotificationType.UnconfiguredTracker:
        return Settings2;
      case InAppNotificationType.TrackerAlert:
        return Timer;
      case InAppNotificationType.StatisticsSummary:
        return Info;
      case InAppNotificationType.HelpResponse:
        return HelpCircle;
      case InAppNotificationType.AnonymousLoginRequest:
        return User;
      case InAppNotificationType.PredictedLow:
        return TrendingDown;
      case InAppNotificationType.SuggestedMealMatch:
        return Utensils;
      case InAppNotificationType.SuggestedTrackerMatch:
        return RefreshCw;
      default:
        return Bell;
    }
  }

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

  const Icon = $derived(getIcon(notification.type));
  const urgencyClasses = $derived(getUrgencyClasses(notification.urgency));
</script>

<div class={cn("flex items-start gap-3 border-b p-3", urgencyClasses)}>
  <div class="shrink-0 mt-0.5">
    <Icon class="h-4 w-4" />
  </div>
  <div class="flex-1 min-w-0">
    <div class="flex items-center justify-between gap-2">
      <span class="text-sm font-medium truncate">
        {notification.title ?? "Notification"}
      </span>
      <span class="text-xs opacity-75 whitespace-nowrap">
        {formatRelativeTime(notification.createdAt)}
      </span>
    </div>
    {#if notification.subtitle}
      <p class="text-xs mt-0.5 opacity-75">
        {notification.subtitle}
      </p>
    {/if}
    {#if notification.actions && notification.actions.length > 0}
      <div class="flex items-center gap-2 mt-2 flex-wrap">
        {#each notification.actions as action (action.actionId)}
          <Button
            variant={getButtonVariant(action.variant)}
            size="sm"
            class="h-6 text-xs px-2"
            onclick={() => onAction?.(action.actionId!)}
          >
            {action.label}
          </Button>
        {/each}
      </div>
    {/if}
  </div>
</div>
