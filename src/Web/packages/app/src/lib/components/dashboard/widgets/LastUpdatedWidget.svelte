<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import {
    BatteryCharging,
    BatteryFull,
    BatteryLow,
    BatteryMedium,
    BatteryWarning,
    Zap,
  } from "lucide-svelte";
  import { timeAgo } from "$lib/utils";
  import { time } from "$lib/utils/formatting";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getCurrentBatteryStatus } from "$api/generated/batteries.generated.remote";

  interface Props {
    /** Override lastUpdated from props instead of realtime store */
    lastUpdated?: number;
  }

  let { lastUpdated }: Props = $props();

  const realtimeStore = getRealtimeStore();
  const displayLastUpdated = $derived(lastUpdated ?? realtimeStore.lastUpdated);

  // Battery data
  const batteryStatusPromise = $derived(
    getCurrentBatteryStatus({ recentMinutes: 30 })
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
</script>

{#await batteryStatusPromise}
  <WidgetCard title="Last Updated">
    <div class="text-2xl font-bold">
      {timeAgo(displayLastUpdated)}
    </div>
    <p class="text-xs text-muted-foreground">
      {time(displayLastUpdated)}
    </p>
  </WidgetCard>
{:then currentStatus}
  {@const hasDevices =
    currentStatus && Object.keys(currentStatus.devices ?? {}).length > 0}
  <WidgetCard title="Last Updated">
    <div class="text-2xl font-bold">
      {timeAgo(displayLastUpdated)}
    </div>
    {#if hasDevices && currentStatus?.min}
      <div class="flex items-center gap-2 mt-1">
        <span class="text-xs text-muted-foreground">
          {extractDeviceName(currentStatus.min.device)}
        </span>
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
      </div>
    {:else}
      <p class="text-xs text-muted-foreground">
        {time(displayLastUpdated)}
      </p>
    {/if}
  </WidgetCard>
{:catch}
  <WidgetCard title="Last Updated">
    <div class="text-2xl font-bold">
      {timeAgo(displayLastUpdated)}
    </div>
    <p class="text-xs text-muted-foreground">
      {time(displayLastUpdated)}
    </p>
  </WidgetCard>
{/await}
