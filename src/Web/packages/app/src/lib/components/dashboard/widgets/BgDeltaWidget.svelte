<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseDelta, getUnitLabel } from "$lib/utils/formatting";
  import { timeAgo, formatTime } from "$lib/utils";
  import { Badge } from "$lib/components/ui/badge";
  import {
    BatteryCharging,
    BatteryFull,
    BatteryLow,
    BatteryMedium,
    BatteryWarning,
    Zap,
    Wifi,
    WifiOff,
  } from "lucide-svelte";
  import { getBatteryCardData } from "$lib/data/battery.remote";

  interface Props {
    /** Override bgDelta from props instead of realtime store */
    bgDelta?: number;
    /** Override lastUpdated from props instead of realtime store */
    lastUpdated?: number;
  }

  let { bgDelta, lastUpdated }: Props = $props();

  const realtimeStore = getRealtimeStore();

  // Use realtime store values as fallback when props not provided
  const rawBgDelta = $derived(bgDelta ?? realtimeStore.bgDelta);
  const displayLastUpdated = $derived(lastUpdated ?? realtimeStore.lastUpdated);
  const units = $derived(glucoseUnits.current);
  const displayBgDelta = $derived(formatGlucoseDelta(rawBgDelta, units));
  const unitLabel = $derived(getUnitLabel(units));

  // Connection status
  const isConnected = $derived(realtimeStore.isConnected);
  const connectionStatus = $derived(realtimeStore.connectionStatus);

  // Battery data
  const batteryDataPromise = $derived(
    getBatteryCardData({ recentMinutes: 30 })
  );

  // Get battery icon component based on level
  function getBatteryIconComponent(level: number | undefined) {
    if (!level) return BatteryWarning;
    if (level >= 95) return BatteryFull;
    if (level >= 50) return BatteryMedium;
    if (level >= 25) return BatteryLow;
    return BatteryWarning;
  }

  // Extract device name from URI
  function extractDeviceName(device: string | undefined): string {
    if (!device) return "Unknown";
    if (device.includes("://")) {
      return device.split("://")[1] || device;
    }
    return device;
  }

  // Connection status indicator color
  const connectionColor = $derived.by(() => {
    switch (connectionStatus) {
      case "connected":
        return "bg-green-500";
      case "connecting":
      case "reconnecting":
        return "bg-yellow-500";
      case "disconnected":
        return "bg-gray-500";
      case "error":
        return "bg-red-500";
      default:
        return "bg-gray-500";
    }
  });
</script>

<WidgetCard title="BG Delta">
  <div class="flex items-center justify-between">
    <div>
      <div class="text-2xl font-bold">
        {displayBgDelta}
      </div>
      <p class="text-xs text-muted-foreground">{unitLabel}</p>
    </div>

    <!-- Connection indicator -->
    <div class="flex items-center gap-1.5">
      <div class="w-2 h-2 rounded-full {connectionColor}"></div>
      {#if isConnected}
        <Wifi class="h-3.5 w-3.5 text-muted-foreground" />
      {:else}
        <WifiOff class="h-3.5 w-3.5 text-muted-foreground" />
      {/if}
    </div>
  </div>

  <!-- Last updated info with battery -->
  {#await batteryDataPromise}
    <div class="flex items-center justify-between mt-2 pt-2 border-t border-border/50">
      <span class="text-xs text-muted-foreground">
        {timeAgo(displayLastUpdated)}
      </span>
      <span class="text-xs text-muted-foreground">
        {formatTime(displayLastUpdated)}
      </span>
    </div>
  {:then data}
    {@const currentStatus = data?.currentStatus}
    {@const hasDevices =
      currentStatus && Object.keys(currentStatus.devices ?? {}).length > 0}
    <div class="flex items-center justify-between mt-2 pt-2 border-t border-border/50">
      <span class="text-xs text-muted-foreground">
        {timeAgo(displayLastUpdated)}
      </span>
      {#if hasDevices && currentStatus?.min}
        <span
          class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full text-xs font-medium {currentStatus.status ===
          'urgent'
            ? 'bg-red-500/20 text-red-400'
            : currentStatus.status === 'warn'
              ? 'bg-yellow-500/20 text-yellow-400'
              : 'bg-green-500/20 text-green-400'}"
        >
          {#if currentStatus.min.isCharging}
            <BatteryCharging class="h-3 w-3" />
          {:else}
            {@const IconComponent = getBatteryIconComponent(currentStatus.level)}
            <IconComponent class="h-3 w-3" />
          {/if}
          {currentStatus.display}
          {#if currentStatus.min.isCharging}
            <Zap class="h-3 w-3" />
          {/if}
        </span>
      {:else}
        <span class="text-xs text-muted-foreground">
          {formatTime(displayLastUpdated)}
        </span>
      {/if}
    </div>
  {:catch}
    <div class="flex items-center justify-between mt-2 pt-2 border-t border-border/50">
      <span class="text-xs text-muted-foreground">
        {timeAgo(displayLastUpdated)}
      </span>
      <span class="text-xs text-muted-foreground">
        {formatTime(displayLastUpdated)}
      </span>
    </div>
  {/await}
</WidgetCard>
