<script lang="ts">
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { cn } from "#lib/utils/index.js";
  import { relativeAge } from "#lib/utils/formatting.js";
  import {
    type InAppNotificationDto,
    NotificationUrgency,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { resolveNotificationIcon } from "#lib/utils/notification-icons.js";
  import { resolveNotificationLabel } from "#lib/utils/notification-labels.js";
  import InstallTiming from "#lib/components/notifications/InstallTiming.svelte";
  import InstanceKeyChanged from "#lib/components/notifications/InstanceKeyChanged.svelte";

  interface Props {
    notification: InAppNotificationDto;
    onAction?: (actionId: string) => void;
  }

  let { notification, onAction }: Props = $props();

  // An alert's ack action mutes it for this member only unless they hold
  // alerts.readwrite, so it is labelled for what the server will do.
  const mutesOnly = $derived(
    notification.type === "alert.firing" &&
      !satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite")
  );

  // Get color classes based on urgency
  function getUrgencyClasses(urgency: NotificationUrgency | undefined): string {
    switch (urgency) {
      case NotificationUrgency.Urgent:
        return "text-severity-urgent bg-severity-urgent/10 border-severity-urgent/20";
      case NotificationUrgency.Hazard:
        return "text-severity-hazard bg-severity-hazard/10 border-severity-hazard/20";
      case NotificationUrgency.Warn:
        return "text-severity-warn bg-severity-warn/10 border-severity-warn/20";
      default:
        return "text-muted-foreground bg-muted/50 border-border";
    }
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
  const releaseVersion = $derived(notification.metadata?.["Version"]?.toString());
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
        {#if notification.type === "system.update_available" && releaseVersion}
          Nocturne {releaseVersion} is available
        {:else if notification.type === "system.release_installed" && releaseVersion}
          Nocturne is now on version {releaseVersion}
        {:else}
          {resolveNotificationLabel(notification.title) || "Notification"}
        {/if}
      </span>
      <span class="text-xs opacity-75 whitespace-nowrap">
        {relativeAge(notification.createdAt)}
      </span>
    </div>
    {#if notification.type === "system.update_available"}
      <p class="text-xs mt-0.5 opacity-75">
        <InstallTiming
          mode={notification.metadata?.["AutoUpdateMode"]?.toString()}
          nextCheckAt={notification.metadata?.["NextCheckAt"]?.toString()}
          checkIntervalSeconds={Number(notification.metadata?.["CheckIntervalSeconds"]) || null}
        />
      </p>
    {:else if notification.type === "system.release_installed"}
      <p class="text-xs mt-0.5 opacity-75">See what changed in this version.</p>
    {:else if notification.type === "system.instance_key_changed" || notification.type === "system.instance_key_changed_platform"}
      <div class="text-xs mt-0.5 opacity-75 space-y-0.5">
        <InstanceKeyChanged
          metadata={notification.metadata}
          platform={notification.type === "system.instance_key_changed_platform"}
        />
      </div>
    {:else if notification.subtitle}
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
            {#if mutesOnly && action.actionId === "ack"}
              Mute for me
            {:else}
              {resolveNotificationLabel(action.label)}
            {/if}
          </Button>
        {/each}
      </div>
    {/if}
  </div>
</div>
