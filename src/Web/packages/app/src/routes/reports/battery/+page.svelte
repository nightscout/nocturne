<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Battery,
    BatteryCharging,
    BatteryFull,
    BatteryLow,
    BatteryMedium,
    BatteryWarning,
    Calendar,
    Clock,
    Zap,
    AlertTriangle,
    RefreshCw,
  } from "lucide-svelte";
  import type { BatteryStatistics, ChargeCycle, DeviceStatus } from "$lib/api";
  import { getBatteryReportData } from "$lib/data/battery.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days is good for battery analysis (typical charge cycle period)
  const reportsParams = requireDateParamsContext(7);

  // State for device selection
  let selectedDevice = $state<string | null>(null);

  // Calculate date range in milliseconds from the params
  const dateRangeMillis = $derived({
    from: new Date(reportsParams.dateRangeInput.from ?? new Date().toISOString()).getTime(),
    to: new Date(reportsParams.dateRangeInput.to ?? new Date().toISOString()).getTime(),
  });

  // Create resource with automatic layout registration
  const batteryResource = contextResource(
    () => getBatteryReportData({
      device: selectedDevice,
      from: dateRangeMillis.from,
      to: dateRangeMillis.to,
      cycleLimit: 50,
    }),
    { errorTitle: "Error Loading Battery Report" }
  );

  // Derived state from resource with explicit types
  const statistics = $derived<BatteryStatistics[]>(batteryResource.current?.statistics ?? []);
  const cycles = $derived<ChargeCycle[]>(batteryResource.current?.cycles ?? []);
  const readings = $derived<DeviceStatus[]>(batteryResource.current?.readings ?? []);

  // Helper for date range display
  const dateRange = $derived({
    from: dateRangeMillis.from,
    to: dateRangeMillis.to,
  });

  function fetchData() {
    batteryResource.refresh();
  }

  // Helper functions
  function formatDuration(minutes?: number | null): string {
    if (!minutes) return "N/A";
    const hours = Math.floor(minutes / 60);
    const mins = Math.round(minutes % 60);
    if (hours === 0) return `${mins}m`;
    if (mins === 0) return `${hours}h`;
    return `${hours}h ${mins}m`;
  }

  function formatDateShort(mills?: number | null): string {
    if (!mills) return "Unknown";
    return new Date(mills).toLocaleDateString([], {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  function getStatusColor(status: string | undefined): string {
    switch (status) {
      case "urgent":
        return "text-red-500";
      case "warn":
        return "text-yellow-500";
      default:
        return "text-green-500";
    }
  }

  function getBatteryIconComponent(
    level: number | undefined,
    isCharging: boolean | undefined
  ) {
    if (isCharging) return BatteryCharging;
    if (!level) return BatteryWarning;
    if (level >= 95) return BatteryFull;
    if (level >= 50) return BatteryMedium;
    if (level >= 25) return BatteryLow;
    return BatteryWarning;
  }

  function extractDeviceName(device: string | undefined): string {
    if (!device) return "Unknown";
    if (device.includes("://")) {
      return device.split("://")[1] || device;
    }
    return device;
  }

  // Derived values
  const allDevices = $derived([
    ...new Set(statistics.map((s) => s.device ?? "")),
  ]);
  const displayedStats = $derived(
    selectedDevice
      ? statistics.filter((s) => s.device === selectedDevice)
      : statistics
  );
</script>

<svelte:head>
  <title>Battery Report - Nocturne</title>
  <meta
    name="description"
    content="Device battery statistics and charge cycle history"
  />
</svelte:head>

{#if batteryResource.current}
<div class="container mx-auto space-y-6 px-4 py-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-3xl font-bold">Battery Report</h1>
      <p class="text-muted-foreground">
        Device battery statistics and charge cycle history
      </p>
    </div>
    <Button variant="outline" size="sm" onclick={fetchData}>
      <RefreshCw class="h-4 w-4 mr-2" />
      Refresh
    </Button>
  </div>

  <!-- Date Range Info -->
  <div class="flex items-center gap-2 text-sm text-muted-foreground">
    <Calendar class="h-4 w-4" />
    <span>
      {new Date(dateRange.from).toLocaleDateString()} – {new Date(
        dateRange.to
      ).toLocaleDateString()}
    </span>
    <span class="text-muted-foreground/50">•</span>
    <span>{readings.length} readings</span>
  </div>

  {#if statistics.length === 0}
    <Card>
      <CardContent class="pt-6">
        <div class="text-center py-8">
          <Battery class="h-12 w-12 text-muted-foreground mx-auto mb-4" />
          <h3 class="text-lg font-medium">No Battery Data Available</h3>
          <p class="text-sm text-muted-foreground mt-2">
            Battery data is collected from devices that report uploader status.
            Make sure your CGM uploader app is sending device status data.
          </p>
        </div>
      </CardContent>
    </Card>
  {:else}
    <!-- Device Filter (if multiple devices) -->
    {#if allDevices.length > 1}
      <div class="flex gap-2 flex-wrap">
        <Button
          variant={selectedDevice === null ? "default" : "outline"}
          size="sm"
          onclick={() => (selectedDevice = null)}
        >
          All Devices
        </Button>
        {#each allDevices as device}
          <Button
            variant={selectedDevice === device ? "default" : "outline"}
            size="sm"
            onclick={() => (selectedDevice = device)}
          >
            {extractDeviceName(device)}
          </Button>
        {/each}
      </div>
    {/if}

    <!-- Statistics Cards -->
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      {#each displayedStats as stat}
        {@const StatIcon = getBatteryIconComponent(
          stat?.level,
          stat?.isCharging
        )}
        <Card>
          <CardHeader class="pb-2">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2">
                <StatIcon class="h-5 w-5 {getStatusColor(stat?.status)}" />
                <CardTitle class="text-base">{stat?.displayName}</CardTitle>
              </div>
              <Badge
                variant={stat?.status === "urgent"
                  ? "destructive"
                  : stat.status === "warn"
                    ? "secondary"
                    : "default"}
              >
                {stat.display}
              </Badge>
            </div>
          </CardHeader>
          <CardContent class="space-y-4">
            <!-- Current Status -->
            <div class="grid grid-cols-2 gap-2 text-sm">
              <div>
                <span class="text-muted-foreground">Current:</span>
                <span class="font-medium ml-1">
                  {stat.currentLevel ?? "?"}%
                  {#if stat.isCharging}
                    <Zap class="inline h-3 w-3 text-yellow-500" />
                  {/if}
                </span>
              </div>
              <div>
                <span class="text-muted-foreground">Readings:</span>
                <span class="font-medium ml-1">{stat.readingCount}</span>
              </div>
            </div>

            <Separator />

            <!-- Statistics -->
            <div class="grid grid-cols-2 gap-2 text-sm">
              {#if stat.averageLevel}
                <div>
                  <span class="text-muted-foreground">Avg level:</span>
                  <span class="font-medium ml-1">
                    {stat.averageLevel.toFixed(0)}%
                  </span>
                </div>
              {/if}
              {#if stat.minLevel !== undefined && stat.maxLevel !== undefined}
                <div>
                  <span class="text-muted-foreground">Range:</span>
                  <span class="font-medium ml-1">
                    {stat.minLevel}% - {stat.maxLevel}%
                  </span>
                </div>
              {/if}
            </div>

            <Separator />

            <!-- Charge Cycle Stats -->
            <div class="space-y-2">
              <h4
                class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
              >
                Charge Patterns
              </h4>
              <div class="grid grid-cols-2 gap-2 text-sm">
                <div>
                  <span class="text-muted-foreground">Cycles:</span>
                  <span class="font-medium ml-1">{stat.chargeCycleCount}</span>
                </div>
                {#if stat.averageTimeBetweenChargesHours}
                  <div>
                    <span class="text-muted-foreground">Avg life:</span>
                    <span class="font-medium ml-1">
                      {formatDuration(stat.averageDischargeDurationMinutes)}
                    </span>
                  </div>
                {/if}
                {#if stat.longestDischargeDurationMinutes}
                  <div>
                    <span class="text-muted-foreground">Longest:</span>
                    <span class="font-medium ml-1 text-green-600">
                      {formatDuration(stat.longestDischargeDurationMinutes)}
                    </span>
                  </div>
                {/if}
                {#if stat.shortestDischargeDurationMinutes}
                  <div>
                    <span class="text-muted-foreground">Shortest:</span>
                    <span class="font-medium ml-1 text-yellow-600">
                      {formatDuration(stat.shortestDischargeDurationMinutes)}
                    </span>
                  </div>
                {/if}
              </div>
            </div>

            <!-- Time in Zones -->
            {#if (stat?.readingCount ?? 0) > 0}
              <Separator />
              <div class="space-y-2">
                <h4
                  class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
                >
                  Time Distribution
                </h4>
                <div class="space-y-1">
                  <div class="flex justify-between text-sm">
                    <span>Above 80%</span>
                    <span class="font-medium text-green-600">
                      {(stat?.timeAbove80Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-green-500"
                      style="width: {stat?.timeAbove80Percent ?? 0}%"
                    ></div>
                  </div>
                  <div class="flex justify-between text-sm">
                    <span>30% - 80%</span>
                    <span class="font-medium">
                      {(stat?.timeBetween30And80Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-blue-500"
                      style="width: {stat?.timeBetween30And80Percent ?? 0}%"
                    ></div>
                  </div>
                  <div class="flex justify-between text-sm">
                    <span>Below 30%</span>
                    <span class="font-medium text-yellow-600">
                      {(stat?.timeBelow30Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-yellow-500"
                      style="width: {stat?.timeBelow30Percent ?? 0}%"
                    ></div>
                  </div>
                </div>
              </div>
            {/if}

            <!-- Warning Events -->
            {#if (stat?.warningEventCount ?? 0) > 0 || (stat?.urgentEventCount ?? 0) > 0}
              <Separator />
              <div class="flex gap-4 text-sm">
                {#if (stat?.warningEventCount ?? 0) > 0}
                  <div class="flex items-center gap-1 text-yellow-600">
                    <AlertTriangle class="h-4 w-4" />
                    <span>{stat?.warningEventCount ?? 0} warnings</span>
                  </div>
                {/if}
                {#if (stat?.urgentEventCount ?? 0) > 0}
                  <div class="flex items-center gap-1 text-red-600">
                    <AlertTriangle class="h-4 w-4" />
                    <span>{stat?.urgentEventCount ?? 0} critical</span>
                  </div>
                {/if}
              </div>
            {/if}
          </CardContent>
        </Card>
      {/each}
    </div>

    <!-- Charge Cycle History -->
    {#if cycles.length > 0}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Clock class="h-5 w-5 text-muted-foreground" />
            Recent Charge Cycles
          </CardTitle>
          <CardDescription>
            History of battery charge and discharge periods
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div class="space-y-3">
            {#each cycles.slice(0, 10) as cycle}
              <div
                class="flex items-center justify-between p-3 rounded-lg border"
              >
                <div class="flex items-center gap-3">
                  <div class="flex flex-col items-center">
                    <BatteryCharging class="h-4 w-4 text-green-500" />
                    <div class="h-6 border-l border-dashed"></div>
                    <Battery class="h-4 w-4 text-muted-foreground" />
                  </div>
                  <div class="space-y-1">
                    <div class="text-sm font-medium">
                      {extractDeviceName(cycle.device)}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      {#if cycle.chargeStartMills}
                        Charged: {formatDateShort(cycle.chargeStartMills)}
                        ({cycle.chargeStartLevel ?? "?"}% → {cycle.chargeEndLevel ??
                          "?"}%)
                      {/if}
                    </div>
                    {#if cycle.dischargeDurationMinutes}
                      <div class="text-xs text-muted-foreground">
                        Lasted: {formatDuration(cycle.dischargeDurationMinutes)}
                        ({cycle.dischargeStartLevel ?? "?"}% → {cycle.dischargeEndLevel ??
                          "?"}%)
                      </div>
                    {/if}
                  </div>
                </div>
                <div class="text-right">
                  {#if cycle.dischargeDurationMinutes}
                    <div class="text-lg font-bold">
                      {formatDuration(cycle.dischargeDurationMinutes)}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      battery life
                    </div>
                  {:else if cycle.chargeDurationMinutes}
                    <div class="text-lg font-bold text-green-600">
                      {formatDuration(cycle.chargeDurationMinutes)}
                    </div>
                    <div class="text-xs text-muted-foreground">charge time</div>
                  {:else}
                    <Badge variant="secondary">In Progress</Badge>
                  {/if}
                </div>
              </div>
            {/each}
          </div>
        </CardContent>
      </Card>
    {/if}

    <!-- Footer -->
    <div class="text-center text-xs text-muted-foreground space-y-1">
      <p>
        Data collected from {allDevices.length} device{allDevices.length !== 1
          ? "s"
          : ""} over {Math.round(
          (dateRange.to - dateRange.from) / (24 * 60 * 60 * 1000)
        )} days
      </p>
      <p class="text-muted-foreground/60">
        Battery statistics are calculated from device status reports sent by
        your uploader app.
      </p>
    </div>
  {/if}
</div>
{/if}
