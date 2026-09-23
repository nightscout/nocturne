<script lang="ts">
  import type { Snippet } from "svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Item, type ItemVariant } from "$lib/components/ui/item";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import {
    CheckCircle,
    Clock,
    AlertCircle,
    Loader2,
    WifiOff,
  } from "lucide-svelte";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";
  import { formatSyncMessage } from "$lib/utils/sync-messages";
  import type { SyncProgressEvent } from "$lib/websocket/types";
  import { formatNumber, formatNumericDate, lastSeen as formatAge } from "$lib/utils/formatting";

  export type DataSourceStatus =
    | "active"
    | "stale"
    | "inactive"
    | "syncing"
    | "error"
    | "backing-off"
    | "disabled"
    | "offline"
    | "configured"
    | "demo";

  interface Props {
    name: string;
    icon: string | undefined;
    status: DataSourceStatus;
    statusMessage?: string;
    totalEntries?: number;
    entriesLast24h?: number;
    lastSeen?: Date;
    lastSyncAttempt?: Date;
    lastSuccessfulSync?: Date;
    totalBreakdown?: Record<string, number>;
    last24hBreakdown?: Record<string, number>;
    syncProgress?: Pick<
      SyncProgressEvent,
      "phase" | "messageType" | "messageParams"
    > | null;
    badges?: Snippet;
    actions?: Snippet;
    onclick?: () => void;
    subtitle?: string;
  }

  let {
    name,
    icon,
    status,
    statusMessage,
    totalEntries,
    entriesLast24h,
    lastSeen,
    lastSyncAttempt,
    lastSuccessfulSync,
    totalBreakdown,
    last24hBreakdown,
    syncProgress,
    badges,
    actions,
    onclick,
    subtitle,
  }: Props = $props();

  function getIconColors(s: DataSourceStatus): {
    bg: string;
    text: string;
  } {
    switch (s) {
      case "active":
      case "syncing":
        return {
          bg: "bg-success/10",
          text: "text-success",
        };
      case "demo":
        return {
          bg: "bg-demo/10",
          text: "text-demo",
        };
      case "configured":
        return {
          bg: "bg-info/10",
          text: "text-info",
        };
      case "stale":
      case "backing-off":
        return {
          bg: "bg-warning/10",
          text: "text-warning",
        };
      case "error":
        return {
          bg: "bg-destructive/10",
          text: "text-destructive",
        };
      case "disabled":
      case "offline":
      case "inactive":
      default:
        return {
          bg: "bg-muted",
          text: "text-muted-foreground",
        };
    }
  }

  function getItemVariant(s: DataSourceStatus): ItemVariant {
    switch (s) {
      case "active":
      case "syncing":
        return "success";
      case "demo":
        return "demo";
      case "error":
        return "destructive";
      default:
        return "outline";
    }
  }


  function formatRelativeTime(date: Date | undefined): string {
    if (!date) return "Never";
    const d = new Date(date);
    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return "Just now";
    if (diffMins < 60)
      return `${diffMins} minute${diffMins !== 1 ? "s" : ""} ago`;
    if (diffHours < 24)
      return `${diffHours} hour${diffHours !== 1 ? "s" : ""} ago`;
    if (diffDays < 7)
      return `${diffDays} day${diffDays !== 1 ? "s" : ""} ago`;

    return formatNumericDate(d);
  }

  const iconColors = $derived(getIconColors(status));
  const itemVariant = $derived(getItemVariant(status));
</script>

<div class="relative">
  <Item variant={itemVariant} size="lg" class="justify-between" {onclick}>
    <div class="flex items-center gap-4 min-w-0 flex-1">
      <div
        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {iconColors.bg}"
      >
        <AppLogo {icon} invertMode />
      </div>
      <div class="min-w-0 flex-1">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="font-medium">{name}</span>
          {#if subtitle}
            <span class="text-xs text-muted-foreground/80">— {subtitle}</span>
          {/if}

          <!-- Status badge -->
          {#if syncProgress?.phase === "Syncing" || status === "syncing"}
            <Badge variant="info">
              <Loader2 class="h-3 w-3 mr-1 animate-spin" />
              Syncing
            </Badge>
          {:else if syncProgress?.phase === "Completed"}
            <Badge variant="success">
              <CheckCircle class="h-3 w-3 mr-1" />
              Sync Complete
            </Badge>
          {:else if syncProgress?.phase === "Failed"}
            <Badge variant="destructive">
              <AlertCircle class="h-3 w-3 mr-1" />
              Sync Failed
            </Badge>
          {:else if status === "backing-off"}
            <Badge variant="warning">
              <Clock class="h-3 w-3 mr-1" />
              Backing Off
            </Badge>
          {:else if status === "error"}
            <Badge variant="destructive">
              <AlertCircle class="h-3 w-3 mr-1" />
              Error
            </Badge>
          {:else if status === "configured"}
            <Badge variant="info">
              <Clock class="h-3 w-3 mr-1" />
              Configured
            </Badge>
          {:else if status === "active"}
            <Badge variant="success">
              <CheckCircle class="h-3 w-3 mr-1" />
              Active
            </Badge>
          {:else if status === "stale"}
            <Badge variant="warning">
              <Clock class="h-3 w-3 mr-1" />
              Stale
            </Badge>
          {:else if status === "disabled"}
            <Badge variant="secondary">
              <WifiOff class="h-3 w-3 mr-1" />
              Disabled
            </Badge>
          {:else if status === "offline"}
            <Badge variant="outline">
              <WifiOff class="h-3 w-3 mr-1" />
              Offline
            </Badge>
          {:else if status === "inactive"}
            <Badge variant="outline">
              <AlertCircle class="h-3 w-3 mr-1" />
              Inactive
            </Badge>
          {/if}

          <!-- Extra badges from caller -->
          {#if badges}
            {@render badges()}
          {/if}
        </div>

        <!-- Metrics line -->
        {#if (syncProgress?.phase === "Syncing") && syncProgress.messageType}
        <p class="text-sm text-info">
          {formatSyncMessage(syncProgress.messageType, syncProgress.messageParams)}
        </p>
        {:else}
        <p class="text-sm text-muted-foreground">
          {#if totalBreakdown && Object.keys(totalBreakdown).length > 0}
            <Tooltip.Root>
              <Tooltip.Trigger variant="term">
                {formatNumber(totalEntries)} records
              </Tooltip.Trigger>
              <Tooltip.Content
                variant="popover"
                class="z-50 overflow-hidden"
              >
                <div class="space-y-1">
                  <div class="font-medium text-xs text-muted-foreground mb-1">
                    Breakdown by type:
                  </div>
                  {#each Object.entries(totalBreakdown) as [type, count] (type)}
                    <div class="flex justify-between gap-4 text-xs">
                      <span>{getDataTypeLabel(type)}</span>
                      <span class="font-mono">
                        {formatNumber(count)}
                      </span>
                    </div>
                  {/each}
                </div>
              </Tooltip.Content>
            </Tooltip.Root>
          {:else}
            {formatNumber(totalEntries)} records
          {/if}

          {#if (entriesLast24h ?? 0) > 0}
            <span class="mx-1">&middot;</span>
            {#if last24hBreakdown && Object.keys(last24hBreakdown).length > 0}
              <Tooltip.Root>
                <Tooltip.Trigger variant="term">
                  {formatNumber(entriesLast24h)} in 24h
                </Tooltip.Trigger>
                <Tooltip.Content
                  variant="popover"
                  class="z-50 overflow-hidden"
                >
                  <div class="space-y-1">
                    <div
                      class="font-medium text-xs text-muted-foreground mb-1"
                    >
                      Last 24h by type:
                    </div>
                    {#each Object.entries(last24hBreakdown) as [type, count] (type)}
                      <div class="flex justify-between gap-4 text-xs">
                        <span>{getDataTypeLabel(type)}</span>
                        <span class="font-mono">
                          {formatNumber(count)}
                        </span>
                      </div>
                    {/each}
                  </div>
                </Tooltip.Content>
              </Tooltip.Root>
            {:else}
              {formatNumber(entriesLast24h)} in 24h
            {/if}
          {/if}

          <span class="mx-1">&middot;</span>
          <Clock class="inline h-3 w-3" />
          {formatAge(lastSuccessfulSync ?? lastSeen)}
        </p>
        {/if}

        <!-- Error detail -->
        {#if status === "error" && statusMessage}
          <div
            class="mt-2 rounded-md bg-destructive/10 p-2 border border-destructive/30"
          >
            <div class="flex items-start gap-2">
              <AlertCircle
                class="h-4 w-4 text-destructive shrink-0 mt-0.5"
              />
              <div class="flex-1 min-w-0">
                <p class="text-sm font-medium text-destructive">
                  Error
                </p>
                <p class="text-xs text-destructive mt-1">
                  {statusMessage}
                </p>
                <p
                  class="text-xs text-destructive/80 mt-1"
                >
                  {#if lastSyncAttempt}
                    Last attempted: {formatRelativeTime(lastSyncAttempt)}
                  {/if}
                  {#if lastSuccessfulSync}
                    {#if lastSyncAttempt}&bull;{/if}
                    Last successful: {formatRelativeTime(lastSuccessfulSync)}
                  {/if}
                </p>
              </div>
            </div>
          </div>
        {/if}
      </div>
    </div>

    <!-- Actions area (only rendered inside the button if no actions snippet) -->
    {#if !actions}
      <div class="flex items-center gap-4 shrink-0">
        <!-- Default: no trailing content -->
      </div>
    {/if}
  </Item>

  <!-- Actions rendered outside the button for proper event handling -->
  {#if actions}
    {@render actions()}
  {/if}
</div>
