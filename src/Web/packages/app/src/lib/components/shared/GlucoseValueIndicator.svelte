<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Previous } from "runed";

  interface Props {
    /** Glucose value to display (already formatted for units) */
    displayValue: string | number;
    /** Raw glucose value in mg/dL for color calculation */
    rawBgMgdl: number;
    /** Whether the data is still loading (no data received yet) */
    isLoading?: boolean;
    /** Whether the data is stale (old) */
    isStale?: boolean;
    /** Whether the connection is disconnected */
    isDisconnected?: boolean;
    /** Whether a sync is in progress */
    isSyncing?: boolean;
    /** Status text to show (e.g., "1 min ago" or "Connection Error") */
    statusText?: string;
    /** Tooltip text for status (e.g., "Last reading: 5 min ago") */
    statusTooltip?: string;
    /** Callback when sync button is clicked (makes status text clickable) */
    onSyncClick?: () => void;
    /** Size variant - 'sm' for sidebar, 'lg' for dashboard */
    size?: "sm" | "lg";
    /** Additional CSS classes for the container */
    class?: string;
  }

  let {
    displayValue,
    rawBgMgdl,
    isLoading = false,
    isStale = false,
    isDisconnected = false,
    isSyncing = false,
    statusText,
    statusTooltip,
    onSyncClick,
    size = "lg",
    class: className = "",
  }: Props = $props();

  // Track previous value using runed's Previous utility
  const previousDisplayValue = new Previous(() => displayValue);

  // Track pulse animation state
  let isPulsing = $state(false);

  // Trigger pulse when value changes (skip initial load)
  const shouldPulse = $derived(
    previousDisplayValue.current !== null &&
      previousDisplayValue.current !== displayValue &&
      !isLoading
  );

  $effect(() => {
    if (shouldPulse) {
      isPulsing = true;
      const timeout = setTimeout(() => {
        isPulsing = false;
      }, 600);
      return () => clearTimeout(timeout);
    }
  });

  // Get background color based on BG value (only when not stale)
  const getBGColor = (bg: number, stale: boolean) => {
    if (stale) return "bg-muted text-muted-foreground";
    if (bg < 70) return "bg-destructive text-destructive-foreground";
    if (bg < 80) return "bg-yellow-500 text-black";
    if (bg > 250) return "bg-destructive text-destructive-foreground";
    if (bg > 180) return "bg-orange-500 text-black";
    return "bg-green-500 text-white";
  };

  // Get border style based on connection status
  const getBorderStyle = (disconnected: boolean, stale: boolean) => {
    const baseClasses = "border-2";
    if (stale && disconnected) {
      return `${baseClasses} border-dashed border-muted-foreground/50 animate-flash-border`;
    }
    if (disconnected) {
      return `${baseClasses} border-dashed border-current`;
    }
    return ""; // No special border when connected
  };

  const sizeClasses = $derived(
    size === "lg" ? "text-4xl px-4 py-2" : "text-3xl px-3 py-1.5"
  );

  const skeletonSizeClasses = $derived(
    size === "lg" ? "h-12 w-20" : "h-10 w-16"
  );
</script>

<!-- Horizontal layout with grid overlay on status text to prevent layout shift when syncing -->
<div class="glucose-value-indicator inline-flex items-center gap-2 {className}">
  {#if isLoading}
    <!-- Loading skeleton -->
    <Skeleton class="rounded-lg {skeletonSizeClasses}" />
    <div class="flex flex-col gap-1">
      <Skeleton class="h-4 w-12" />
      <Skeleton class="h-3 w-16" />
    </div>
  {:else}
    <!-- Actual value display -->
    <div
      class="font-bold rounded-lg {sizeClasses} {getBGColor(
        rawBgMgdl,
        isStale
      )} {getBorderStyle(isDisconnected, isStale)} {isPulsing
        ? 'pulse-once'
        : ''}"
    >
      {displayValue}
    </div>

    {#if statusText}
      <Tooltip.Root>
        <Tooltip.Trigger>
          {#snippet child({ props })}
            {#if onSyncClick}
              <!-- Grid overlay approach: both states occupy same cell, only one visible -->
              <button
                {...props}
                type="button"
                onclick={onSyncClick}
                disabled={isSyncing}
                class="text-xs transition-colors grid"
              >
                <!-- Normal state text (invisible when syncing) -->
                <span
                  class="col-start-1 row-start-1 {isSyncing
                    ? 'invisible'
                    : isDisconnected
                      ? 'text-destructive font-medium hover:text-destructive/80 hover:underline'
                      : 'text-muted-foreground hover:text-foreground hover:underline'}"
                >
                  {statusText}
                </span>
                <!-- Syncing state text (invisible when not syncing) -->
                <span
                  class="col-start-1 row-start-1 animate-pulse text-primary font-medium {isSyncing
                    ? ''
                    : 'invisible'}"
                >
                  Syncing...
                </span>
              </button>
            {:else}
              <span
                {...props}
                class="text-xs cursor-help {isDisconnected
                  ? 'text-destructive font-medium'
                  : 'text-muted-foreground'}"
              >
                {statusText}
              </span>
            {/if}
          {/snippet}
        </Tooltip.Trigger>
        <Tooltip.Content side="bottom">
          <p>
            {onSyncClick ? "Click to sync data" : statusTooltip || statusText}
          </p>
        </Tooltip.Content>
      </Tooltip.Root>
    {/if}
  {/if}
</div>

<style>
  @keyframes flash-border {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0.3;
    }
  }

  .animate-flash-border {
    animation: flash-border 1.5s ease-in-out infinite;
  }

  @keyframes pulse-once {
    0% {
      transform: scale(1);
      filter: brightness(1);
    }
    50% {
      transform: scale(1.05);
      filter: brightness(1.15);
    }
    100% {
      transform: scale(1);
      filter: brightness(1);
    }
  }

  .pulse-once {
    animation: pulse-once 0.6s ease-in-out;
  }
</style>
