<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import {
    BatteryCharging,
    BatteryFull,
    BatteryLow,
    BatteryMedium,
    BatteryWarning,
    Zap,
  } from "lucide-svelte";
  import { formatTime, timeAgo } from "$lib/utils";
  import { formatGlucoseDelta, getUnitLabel } from "$lib/utils/formatting";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import WebSocketStatus from "$lib/components/WebSocketStatus.svelte";
  import { getBatteryCardData } from "$api/battery.remote";

  let {
    bgDelta,
    lastUpdated,
    showWebSocketStatus = true,
  }: {
    bgDelta?: number;
    lastUpdated?: number;
    showWebSocketStatus?: boolean;
  } = $props();

  const realtimeStore = getRealtimeStore();

  // Use realtime store values as fallback when props not provided
  const rawBgDelta = $derived(bgDelta ?? realtimeStore.bgDelta);
  const units = $derived(glucoseUnits.current);
  const displayBgDelta = $derived(formatGlucoseDelta(rawBgDelta, units));
  const unitLabel = $derived(getUnitLabel(units));
  const displayLastUpdated = $derived(lastUpdated ?? realtimeStore.lastUpdated);

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
</script>

<div class="grid grid-cols-1 md:grid-cols-3 gap-4">
  <Card>
    <CardHeader class="pb-2">
      <CardTitle class="text-sm font-medium">BG Delta</CardTitle>
    </CardHeader>
    <CardContent>
      <div class="text-2xl font-bold">
        {displayBgDelta}
      </div>
      <p class="text-xs text-muted-foreground">{unitLabel}</p>
    </CardContent>
  </Card>

  <!-- Last Updated Card with Battery Info -->
  {#await batteryDataPromise}
    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium">Last Updated</CardTitle>
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold">
          {timeAgo(displayLastUpdated)}
        </div>
        <p class="text-xs text-muted-foreground">
          {formatTime(displayLastUpdated)}
        </p>
      </CardContent>
    </Card>
  {:then data}
    {@const currentStatus = data?.currentStatus}
    {@const hasDevices =
      currentStatus && Object.keys(currentStatus.devices ?? {}).length > 0}
    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium">Last Updated</CardTitle>
      </CardHeader>
      <CardContent>
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
                {@const IconComponent = getBatteryIconComponent(
                  currentStatus.level
                )}
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
            {formatTime(displayLastUpdated)}
          </p>
        {/if}
      </CardContent>
    </Card>
  {:catch}
    <Card>
      <CardHeader class="pb-2">
        <CardTitle class="text-sm font-medium">Last Updated</CardTitle>
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold">
          {timeAgo(displayLastUpdated)}
        </div>
        <p class="text-xs text-muted-foreground">
          {formatTime(displayLastUpdated)}
        </p>
      </CardContent>
    </Card>
  {/await}

  {#if showWebSocketStatus}
    <WebSocketStatus />
  {/if}
</div>
