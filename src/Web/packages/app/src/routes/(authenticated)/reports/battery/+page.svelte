<script lang="ts">
  import { formatLocale, formatNumericDate } from "$lib/utils/formatting";
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
  import type { BatteryStatistics, ChargeCycle, BatteryReading } from "$lib/api";
  import { getBatteryReportData } from "$api/battery.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { formatMinutesDuration } from "$lib/utils/duration";
  import { Artwork } from "@nocturne/watercolour";
  import { batteryArtwork } from "$lib/watercolour-icons";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days is good for battery analysis (typical charge cycle period)
  const reportsParams = requireDateParamsContext(7);

  let selectedDevice = $state<string | null>(null);

  const batteryResource = contextResource(
    () => getBatteryReportData({
      device: selectedDevice,
      from: reportsParams.dateRangeMillis.from,
      to: reportsParams.dateRangeMillis.to,
      cycleLimit: 50,
    }),
    { errorTitle: "Error Loading Battery Report" }
  );

  const statistics = $derived<BatteryStatistics[]>(batteryResource.current?.statistics ?? []);
  const cycles = $derived<ChargeCycle[]>(batteryResource.current?.cycles ?? []);
  const readings = $derived<BatteryReading[]>(batteryResource.current?.readings ?? []);

  const dateRange = $derived(reportsParams.dateRangeMillis);

  function fetchData() {
    batteryResource.refresh();
  }

  const formatDuration = (minutes?: number | null) =>
    minutes ? formatMinutesDuration(minutes) : "N/A";

  function formatDateShort(mills?: number | null): string {
    if (!mills) return "Unknown";
    return new Date(mills).toLocaleDateString(formatLocale(), {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
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
<div class="@container container mx-auto space-y-6 p-3 @md:p-6">
  <div class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between print:hidden">
    <div>
      <h1 class="text-3xl font-bold">Battery Report</h1>
      <p class="text-muted-foreground">
        Device battery statistics and charge cycle history
      </p>
    </div>
    <Button
      variant="outline"
      size="sm"
      onclick={fetchData}
      class="shrink-0"
    >
      <RefreshCw class="h-4 w-4 mr-2" />
      Refresh
    </Button>
  </div>

  <div class="flex items-center gap-2 text-sm text-muted-foreground print:hidden">
    <Calendar class="h-4 w-4" />
    <span>
      {formatNumericDate(new Date(dateRange.from))} – {formatNumericDate(new Date(
        dateRange.to
      ))}
    </span>
    <span class="text-muted-foreground/50">•</span>
    <span>{readings.length} readings</span>
  </div>

  {#if statistics.length === 0}
    <Card>
      <CardContent class="pt-6">
        <div class="text-center py-8">
          <Artwork
            icon={batteryArtwork}
            palette="water"
            motion="auto"
            autoplay="once"
            class="mx-auto mb-4 size-48"
          />
          <h3 class="text-lg font-medium">No Battery Data Available</h3>
          <p class="text-sm text-muted-foreground mt-2">
            Battery data is collected from devices that report uploader status.
            Make sure your CGM uploader app is sending device status data.
          </p>
        </div>
      </CardContent>
    </Card>
  {:else}
    {#if allDevices.length > 1}
      <div class="flex gap-2 flex-wrap print:hidden">
        <Button
          variant={selectedDevice === null ? "default" : "outline"}
          size="sm"
          onclick={() => (selectedDevice = null)}
        >
          All Devices
        </Button>
        {#each allDevices as device (device)}
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

    <div class="grid grid-cols-1 @xl:grid-cols-2 @4xl:grid-cols-3 print:grid-cols-2 gap-4">
      {#each displayedStats as stat, i (i)}
        {@const StatIcon = getBatteryIconComponent(
          stat?.level,
          stat?.isCharging
        )}
        <Card>
          <CardHeader class="pb-2">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2">
                <span class="battery-status" data-status={stat?.status ?? ""}>
                  <StatIcon class="h-5 w-5" />
                </span>
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
            <div class="grid grid-cols-2 gap-2 text-sm">
              <div>
                <span class="text-muted-foreground">Current:</span>
                <span class="font-medium ml-1">
                  {stat.currentLevel ?? "?"}%
                  {#if stat.isCharging}
                    <Zap class="inline h-3 w-3" />
                  {/if}
                </span>
              </div>
              <div>
                <span class="text-muted-foreground">Readings:</span>
                <span class="font-medium ml-1">{stat.readingCount}</span>
              </div>
            </div>

            <Separator />

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

            <div class="space-y-2">
              <h4 class="text-sm font-medium text-muted-foreground">
                Charge Patterns
              </h4>
              <div class="grid grid-cols-2 gap-2 text-sm">
                <div>
                  <span class="text-muted-foreground">Cycles:</span>
                  <span class="font-medium ml-1">{stat.chargeCycleCount}</span>
                </div>
                {#if stat.averageDischargeDurationMinutes}
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
                    <span class="font-medium ml-1">
                      {formatDuration(stat.longestDischargeDurationMinutes)}
                    </span>
                  </div>
                {/if}
                {#if stat.shortestDischargeDurationMinutes}
                  <div>
                    <span class="text-muted-foreground">Shortest:</span>
                    <span class="font-medium ml-1">
                      {formatDuration(stat.shortestDischargeDurationMinutes)}
                    </span>
                  </div>
                {/if}
              </div>
            </div>

            {#if (stat?.readingCount ?? 0) > 0}
              <Separator />
              <div class="space-y-2">
                <h4 class="text-sm font-medium text-muted-foreground">
                  Time Distribution
                </h4>
                <div class="space-y-1">
                  <div class="flex justify-between text-sm">
                    <span>Above 80%</span>
                    <span class="font-medium tabular-nums">
                      {(stat?.timeAbove80Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-success w-(--share)"
                      style:--share="{stat?.timeAbove80Percent ?? 0}%"
                    ></div>
                  </div>
                  <div class="flex justify-between text-sm">
                    <span>30% - 80%</span>
                    <span class="font-medium tabular-nums">
                      {(stat?.timeBetween30And80Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-info w-(--share)"
                      style:--share="{stat?.timeBetween30And80Percent ?? 0}%"
                    ></div>
                  </div>
                  <div class="flex justify-between text-sm">
                    <span>Below 30%</span>
                    <span class="font-medium tabular-nums">
                      {(stat?.timeBelow30Percent ?? 0).toFixed(1)}%
                    </span>
                  </div>
                  <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                      class="h-full bg-warning w-(--share)"
                      style:--share="{stat?.timeBelow30Percent ?? 0}%"
                    ></div>
                  </div>
                </div>
              </div>
            {/if}

            {#if (stat?.warningEventCount ?? 0) > 0 || (stat?.urgentEventCount ?? 0) > 0}
              <Separator />
              <div class="flex gap-4 text-sm">
                {#if (stat?.warningEventCount ?? 0) > 0}
                  <div class="flex items-center gap-1 text-warning print:text-foreground">
                    <AlertTriangle class="h-4 w-4" />
                    <span>
                      {stat?.warningEventCount ?? 0}
                      {stat?.warningEventCount === 1 ? "warning" : "warnings"}
                    </span>
                  </div>
                {/if}
                {#if (stat?.urgentEventCount ?? 0) > 0}
                  <div class="flex items-center gap-1 text-destructive print:text-foreground">
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
          <ul class="m-0 list-none divide-y divide-border border-y border-border p-0">
            {#each cycles.slice(0, 10) as cycle (cycle.id)}
              <li
                class="flex flex-col gap-2 py-3 @md:flex-row @md:items-center @md:justify-between"
              >
                <div class="space-y-1">
                  <div class="text-sm font-medium">
                    {extractDeviceName(cycle.device)}
                  </div>
                  <div class="text-xs text-muted-foreground tabular-nums">
                    {#if cycle.chargeStartMills}
                      Charged: {formatDateShort(cycle.chargeStartMills)}
                      ({cycle.chargeStartLevel ?? "?"}% to {cycle.chargeEndLevel ??
                        "?"}%)
                    {/if}
                  </div>
                  {#if cycle.dischargeDurationMinutes}
                    <div class="text-xs text-muted-foreground tabular-nums">
                      Lasted: {formatDuration(cycle.dischargeDurationMinutes)}
                      ({cycle.dischargeStartLevel ?? "?"}% to {cycle.dischargeEndLevel ??
                        "?"}%)
                    </div>
                  {/if}
                </div>
                <div class="@md:shrink-0 @md:text-right">
                  {#if cycle.dischargeDurationMinutes}
                    <div class="text-lg font-semibold tabular-nums">
                      {formatDuration(cycle.dischargeDurationMinutes)}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      battery life
                    </div>
                  {:else if cycle.chargeDurationMinutes}
                    <div class="text-lg font-semibold tabular-nums">
                      {formatDuration(cycle.chargeDurationMinutes)}
                    </div>
                    <div class="text-xs text-muted-foreground">charge time</div>
                  {:else}
                    <Badge variant="secondary">In Progress</Badge>
                  {/if}
                </div>
              </li>
            {/each}
          </ul>
        </CardContent>
      </Card>
    {/if}

    <div class="text-center text-xs text-muted-foreground space-y-1">
      <p>
        Data collected from {allDevices.length} device{allDevices.length !== 1
          ? "s"
          : ""} over {reportsParams.dayCount} days
      </p>
      <p class="text-muted-foreground/60">
        Battery statistics are calculated from device status reports sent by
        your uploader app.
      </p>
    </div>
  {/if}
</div>
{/if}

<style>
  /* Battery status is a backend enum; the colour comes from the theme's status
     vars keyed off data-status. */
  .battery-status {
    color: var(--status-normal);
  }
  .battery-status[data-status="warn"] {
    color: var(--status-warning);
  }
  .battery-status[data-status="urgent"] {
    color: var(--status-critical);
  }
</style>
