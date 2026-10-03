<script lang="ts">
  import type { Snippet } from "svelte";
  import * as Tooltip from "./tooltip";
  import { Skeleton } from "./skeleton";
  import { glucoseTileFill, type GlucoseTileVariant } from "../../glucose";

  interface Props {
    /** Glucose value to display (already formatted for units) */
    displayValue: string | number;
    /** Fill from the server's classification; see `glucoseTileVariant` */
    variant?: GlucoseTileVariant;
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
    /**
     * Size variant - 'xs' for collapsed sidebar, 'sm' for sidebar, 'lg' for dashboard, 'xl' for a
     * tile that fills its container's width
     */
    size?: "xs" | "sm" | "lg" | "xl";
    /**
     * Painted over the range fill and under the value, for a host that has artwork to lay there,
     * when `glucoseTileFill` says the tile is painted.
     */
    background?: Snippet;
    /** Set after the value inside the tile, in the tile's own foreground, e.g. a trend arrow */
    trailing?: Snippet;
    /** Additional CSS classes for the container */
    class?: string;
  }

  let {
    displayValue,
    variant = "neutral",
    isLoading = false,
    isStale = false,
    isDisconnected = false,
    isSyncing = false,
    statusText,
    statusTooltip,
    onSyncClick,
    size = "lg",
    background,
    trailing,
    class: className = "",
  }: Props = $props();

  // Pulse the tile once whenever the value changes (skipping the initial load). Tracked with a
  // plain previous-value field rather than a dependency, so the design system stays standalone.
  let isPulsing = $state(false);
  let previousValue: string | number | null = null;
  $effect(() => {
    const value = displayValue;
    if (previousValue !== null && previousValue !== value && !isLoading) {
      previousValue = value;
      isPulsing = true;
      const timeout = setTimeout(() => {
        isPulsing = false;
      }, 600);
      return () => clearTimeout(timeout);
    }
    previousValue = value;
  });

  const variantClasses: Record<GlucoseTileVariant, string> = {
    "very-low": "bg-glucose-very-low text-glucose-very-low-foreground",
    low: "bg-glucose-low text-glucose-low-foreground",
    "in-range": "bg-glucose-in-range text-glucose-in-range-foreground",
    high: "bg-glucose-high text-glucose-high-foreground",
    "very-high": "bg-glucose-very-high text-glucose-very-high-foreground",
    neutral: "bg-muted text-muted-foreground",
  };

  const tileFill = $derived(glucoseTileFill(variant, { stale: isStale, disconnected: isDisconnected }));
  const fillClasses = $derived(variantClasses[tileFill.fill]);
  const showBackground = $derived(background !== undefined && tileFill.painted);

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

  const sizeClasses = $derived.by(() => {
    if (size === "xl")
      return "flex min-w-0 flex-1 items-center justify-center gap-2 px-2 py-4 text-2xl leading-none tabular-nums @[9rem]:gap-3 @[9rem]:px-3 @[9rem]:text-4xl @[12rem]:text-5xl @[16rem]:px-6 @[16rem]:py-5 @[16rem]:text-6xl @[20rem]:text-7xl";
    if (size === "lg") return "text-4xl px-4 py-2";
    if (size === "xs") return "text-base px-1.5 py-1";
    return "text-3xl px-3 py-1.5";
  });

  const skeletonSizeClasses = $derived.by(() => {
    if (size === "xl") return "h-28 flex-1";
    if (size === "lg") return "h-12 w-20";
    if (size === "xs") return "h-8 w-10";
    return "h-10 w-16";
  });
</script>

<!-- Horizontal layout with grid overlay on status text to prevent layout shift when syncing -->
<!-- The xl tile sizes its text from this box, not the viewport: a dashboard cell is as narrow as a
     third of a tablet, and inline-size containment lets it shrink below its own text. -->
<div
  data-slot="glucose-value-indicator"
  class="inline-flex items-center gap-2 {size === 'xl' ? '@container w-full min-w-0' : ''} {className}"
>
  {#if isLoading}
    <!-- Loading skeleton -->
    <Skeleton class="rounded-lg {skeletonSizeClasses}" />
    {#if size !== "xl"}
      <div class="flex flex-col gap-1">
        <Skeleton class="h-4 w-12" />
        <Skeleton class="h-3 w-16" />
      </div>
    {/if}
  {:else}
    <!-- Actual value display -->
    <div
      class="font-bold rounded-lg {showBackground ? 'relative isolate overflow-hidden' : ''} {sizeClasses} {fillClasses} {getBorderStyle(
        isDisconnected,
        isStale
      )} {isPulsing
        ? 'pulse-once'
        : ''}"
    >
      {#if showBackground}
        <!-- No z-index here: a stacking context would isolate the host's blend modes from the fill. -->
        <span aria-hidden="true" class="pointer-events-none absolute inset-0">
          {@render background?.()}
        </span>
        <span class="relative">{displayValue}</span>
      {:else}
        {displayValue}
      {/if}
      {#if trailing}
        <span class="relative inline-flex">{@render trailing()}</span>
      {/if}
    </div>

    {#if statusText}
      <Tooltip.Root>
        <Tooltip.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
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
