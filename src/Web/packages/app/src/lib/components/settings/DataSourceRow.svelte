<script lang="ts">
  import type { Snippet } from "svelte";
  import { Badge } from "$lib/components/ui/badge";
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
  import type { SyncMessageType } from "$lib/websocket/types";

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
    syncProgress?: {
      phase: string;
      currentDataType: string | null;
      completedDataTypes: string[];
      totalDataTypes: number;
      itemsSyncedSoFar: Record<string, number>;
      messageType: SyncMessageType | null;
      messageParams: Record<string, string> | null;
    } | null;
    badges?: Snippet;
    actions?: Snippet;
    onclick?: () => void;
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
  }: Props = $props();

  function getIconColors(s: DataSourceStatus): {
    bg: string;
    text: string;
  } {
    switch (s) {
      case "active":
      case "syncing":
        return {
          bg: "bg-green-100 dark:bg-green-900/30",
          text: "text-green-600 dark:text-green-400",
        };
      case "demo":
        return {
          bg: "bg-purple-100 dark:bg-purple-900/30",
          text: "text-purple-600 dark:text-purple-400",
        };
      case "configured":
        return {
          bg: "bg-blue-100 dark:bg-blue-900/30",
          text: "text-blue-600 dark:text-blue-400",
        };
      case "stale":
      case "backing-off":
        return {
          bg: "bg-yellow-100 dark:bg-yellow-900/30",
          text: "text-yellow-600 dark:text-yellow-400",
        };
      case "error":
        return {
          bg: "bg-red-100 dark:bg-red-900/30",
          text: "text-red-600 dark:text-red-400",
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

  function getBorderClass(s: DataSourceStatus): string {
    switch (s) {
      case "active":
      case "syncing":
        return "border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20";
      case "demo":
        return "border-purple-200 dark:border-purple-800 bg-purple-50/50 dark:bg-purple-950/20";
      case "error":
        return "border-red-300 dark:border-red-700 bg-red-50/50 dark:bg-red-950/20";
      default:
        return "";
    }
  }

  function formatLastSeen(date?: Date): string {
    if (!date) return "Never";
    const d = new Date(date);
    const diff = Date.now() - d.getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 1) return "Just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString();
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

    return d.toLocaleDateString();
  }

  const iconColors = $derived(getIconColors(status));
  const borderClass = $derived(getBorderClass(status));
</script>

<div class="relative">
  <button
    class="w-full flex items-center justify-between p-4 rounded-lg border bg-card hover:bg-accent/50 transition-colors text-left {borderClass}"
    {onclick}
  >
    <div class="flex items-center gap-4 min-w-0 flex-1">
      <div
        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {iconColors.bg}"
      >
        <AppLogo {icon} />
      </div>
      <div class="min-w-0 flex-1">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="font-medium">{name}</span>

          <!-- Status badge -->
          {#if syncProgress?.phase === "Syncing" || status === "syncing"}
            <Badge
              class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
            >
              <Loader2 class="h-3 w-3 mr-1 animate-spin" />
              {#if syncProgress?.currentDataType}
                Syncing {syncProgress.currentDataType}
                ({syncProgress.completedDataTypes.length}/{syncProgress.totalDataTypes})
              {:else}
                Syncing
              {/if}
            </Badge>
          {:else if syncProgress?.phase === "Completed"}
            <Badge
              class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
            >
              <CheckCircle class="h-3 w-3 mr-1" />
              Sync Complete
            </Badge>
          {:else if syncProgress?.phase === "Failed"}
            <Badge variant="destructive" class="text-xs">
              <AlertCircle class="h-3 w-3 mr-1" />
              Sync Failed
            </Badge>
          {:else if status === "backing-off"}
            <Badge
              variant="secondary"
              class="bg-orange-100 text-orange-800 dark:bg-orange-900 dark:text-orange-100 text-xs"
            >
              <Clock class="h-3 w-3 mr-1" />
              Backing Off
            </Badge>
          {:else if status === "error"}
            <Badge variant="destructive" class="text-xs">
              <AlertCircle class="h-3 w-3 mr-1" />
              Error
            </Badge>
          {:else if status === "configured"}
            <Badge
              variant="secondary"
              class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
            >
              <Clock class="h-3 w-3 mr-1" />
              Configured
            </Badge>
          {:else if status === "active"}
            <Badge
              class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
            >
              <CheckCircle class="h-3 w-3 mr-1" />
              Active
            </Badge>
          {:else if status === "stale"}
            <Badge
              variant="secondary"
              class="bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-100 text-xs"
            >
              <Clock class="h-3 w-3 mr-1" />
              Stale
            </Badge>
          {:else if status === "disabled"}
            <Badge
              variant="secondary"
              class="bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-100 text-xs"
            >
              <WifiOff class="h-3 w-3 mr-1" />
              Disabled
            </Badge>
          {:else if status === "offline"}
            <Badge variant="outline" class="text-xs">
              <WifiOff class="h-3 w-3 mr-1" />
              Offline
            </Badge>
          {:else if status === "inactive"}
            <Badge variant="outline" class="text-xs">
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
        <p class="text-sm text-blue-600 dark:text-blue-400">
          {formatSyncMessage(syncProgress.messageType, syncProgress.messageParams)}
        </p>
        {:else}
        <p class="text-sm text-muted-foreground">
          {#if totalBreakdown && Object.keys(totalBreakdown).length > 0}
            <Tooltip.Root>
              <Tooltip.Trigger>
                <span
                  class="cursor-help underline decoration-dotted decoration-muted-foreground/50"
                >
                  {(totalEntries ?? 0).toLocaleString()} records
                </span>
              </Tooltip.Trigger>
              <Tooltip.Portal>
                <Tooltip.Content
                  class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                >
                  <div class="space-y-1">
                    <div class="font-medium text-xs text-muted-foreground mb-1">
                      Breakdown by type:
                    </div>
                    {#each Object.entries(totalBreakdown) as [type, count]}
                      <div class="flex justify-between gap-4 text-xs">
                        <span>{getDataTypeLabel(type)}</span>
                        <span class="font-mono">
                          {count?.toLocaleString()}
                        </span>
                      </div>
                    {/each}
                  </div>
                </Tooltip.Content>
              </Tooltip.Portal>
            </Tooltip.Root>
          {:else}
            {(totalEntries ?? 0).toLocaleString()} records
          {/if}

          {#if (entriesLast24h ?? 0) > 0}
            <span class="mx-1">&middot;</span>
            {#if last24hBreakdown && Object.keys(last24hBreakdown).length > 0}
              <Tooltip.Root>
                <Tooltip.Trigger>
                  <span
                    class="cursor-help underline decoration-dotted decoration-muted-foreground/50"
                  >
                    {(entriesLast24h ?? 0).toLocaleString()} in 24h
                  </span>
                </Tooltip.Trigger>
                <Tooltip.Portal>
                  <Tooltip.Content
                    class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                  >
                    <div class="space-y-1">
                      <div
                        class="font-medium text-xs text-muted-foreground mb-1"
                      >
                        Last 24h by type:
                      </div>
                      {#each Object.entries(last24hBreakdown) as [type, count]}
                        <div class="flex justify-between gap-4 text-xs">
                          <span>{getDataTypeLabel(type)}</span>
                          <span class="font-mono">
                            {count?.toLocaleString()}
                          </span>
                        </div>
                      {/each}
                    </div>
                  </Tooltip.Content>
                </Tooltip.Portal>
              </Tooltip.Root>
            {:else}
              {(entriesLast24h ?? 0).toLocaleString()} in 24h
            {/if}
          {/if}

          <span class="mx-1">&middot;</span>
          <Clock class="inline h-3 w-3" />
          {formatLastSeen(lastSeen)}
        </p>
        {/if}
        {#if syncProgress?.phase === "Syncing" && Object.keys(syncProgress.itemsSyncedSoFar).length > 0}
          <p class="text-xs text-blue-600 dark:text-blue-400">
            {Object.entries(syncProgress.itemsSyncedSoFar)
              .map(([type, count]) => `${count.toLocaleString()} ${type}`)
              .join(", ")} synced so far
          </p>
        {/if}

        <!-- Error detail -->
        {#if status === "error" && statusMessage}
          <div
            class="mt-2 rounded-md bg-red-50 dark:bg-red-950/30 p-2 border border-red-200 dark:border-red-800"
          >
            <div class="flex items-start gap-2">
              <AlertCircle
                class="h-4 w-4 text-red-600 dark:text-red-400 shrink-0 mt-0.5"
              />
              <div class="flex-1 min-w-0">
                <p class="text-sm font-medium text-red-800 dark:text-red-200">
                  Error
                </p>
                <p class="text-xs text-red-700 dark:text-red-300 mt-1">
                  {statusMessage}
                </p>
                <p
                  class="text-xs text-red-600/80 dark:text-red-400/80 mt-1"
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
  </button>

  <!-- Actions rendered outside the button for proper event handling -->
  {#if actions}
    {@render actions()}
  {/if}
</div>
