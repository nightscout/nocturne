<script lang="ts">
  import { Tooltip } from "layerchart";
  import { cn } from "$lib/utils";
  import { goto } from "$app/navigation";
  import { BasalDeliveryOrigin, type BasalPoint } from "$lib/api";
  import type { TimeSeriesPoint } from "$lib/data/chart-data.remote";

  interface BolusMarker {
    time: Date;
    insulin: number;
  }

  interface CarbMarker {
    time: Date;
    carbs: number;
  }

  interface DeviceEventMarker {
    time: Date;
    eventType: string;
    notes?: string;
    config: { color: string };
  }

  interface PumpModeSpan {
    startTime: Date;
    endTime?: Date | null;
    state: string;
    color: string;
  }

  interface OverrideSpan {
    startTime: Date;
    endTime?: Date | null;
    state: string;
    color: string;
  }

  interface ProfileSpan {
    startTime: Date;
    endTime?: Date | null;
    profileName: string;
    color: string;
  }

  interface ActivitySpan {
    id: string;
    startTime: Date;
    endTime?: Date | null;
    category: string;
    state: string;
    color: string;
  }

  interface SystemEvent {
    time: Date;
    eventType: string;
    description?: string;
    code?: string;
    color: string;
  }

  interface Props {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    context: any;
    // Data finders - functions that find relevant data at a given time
    findBasalValue: (time: Date) => BasalPoint | undefined;
    findIobValue: (time: Date) => TimeSeriesPoint | undefined;
    findCobValue: (time: Date) => TimeSeriesPoint | undefined;
    findNearbyBolus: (time: Date) => BolusMarker | undefined;
    findNearbyCarbs: (time: Date) => CarbMarker | undefined;
    findNearbyDeviceEvent: (time: Date) => DeviceEventMarker | undefined;
    findActivePumpMode: (time: Date) => PumpModeSpan | undefined;
    findActiveOverride: (time: Date) => OverrideSpan | undefined;
    findActiveProfile: (time: Date) => ProfileSpan | undefined;
    findActiveActivities: (time: Date) => ActivitySpan[];
    findNearbySystemEvent: (time: Date) => SystemEvent | undefined;
    // Visibility toggles
    showBolus: boolean;
    showCarbs: boolean;
    showDeviceEvents: boolean;
    showIob: boolean;
    showCob: boolean;
    showBasal: boolean;
    showPumpModes: boolean;
    showOverrideSpans: boolean;
    showProfileSpans: boolean;
    showActivitySpans: boolean;
    showAlarms: boolean;
    // Stale data indicator
    staleBasalData: { start: Date; end: Date } | null;
  }

  let {
    context,
    findBasalValue,
    findIobValue,
    findCobValue,
    findNearbyBolus,
    findNearbyCarbs,
    findNearbyDeviceEvent,
    findActivePumpMode,
    findActiveOverride,
    findActiveProfile,
    findActiveActivities,
    findNearbySystemEvent,
    showBolus,
    showCarbs,
    showDeviceEvents,
    showIob,
    showCob,
    showBasal,
    showPumpModes,
    showOverrideSpans,
    showProfileSpans,
    showActivitySpans,
    showAlarms,
    staleBasalData,
  }: Props = $props();
</script>

<Tooltip.Root
  {context}
  class="bg-popover/95 border border-border rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
>
  {#snippet children({ data })}
    {@const activeBasal = findBasalValue(data.time)}
    {@const activeIob = findIobValue(data.time)}
    {@const activeCob = findCobValue(data.time)}
    {@const activePumpMode = findActivePumpMode(data.time)}
    {@const activeOverride = findActiveOverride(data.time)}
    {@const activeProfile = findActiveProfile(data.time)}
    {@const activeActivities = findActiveActivities(data.time)}
    {@const nearbyBolus = findNearbyBolus(data.time)}
    {@const nearbyCarbs = findNearbyCarbs(data.time)}
    {@const nearbyDeviceEvent = findNearbyDeviceEvent(data.time)}
    {@const nearbySystemEvent = findNearbySystemEvent(data.time)}

    <Tooltip.Header
      value={data?.time}
      format="minute"
      class="text-popover-foreground border-b border-border pb-1 mb-1 text-sm font-semibold"
    />
    <Tooltip.List>
      {#if data?.sgv}
        <Tooltip.Item
          label="Glucose"
          value={data.sgv}
          format="integer"
          color="var(--glucose-in-range)"
          class="text-popover-foreground font-bold"
        />
      {/if}
      {#if showBolus && nearbyBolus}
        <Tooltip.Item
          label="Bolus"
          value={`${nearbyBolus.insulin.toFixed(1)}U`}
          color="var(--insulin-bolus)"
          class="font-medium"
        />
      {/if}
      {#if showCarbs && nearbyCarbs}
        <Tooltip.Item
          label="Carbs"
          value={`${nearbyCarbs.carbs}g`}
          color="var(--carbs)"
          class="font-medium"
        />
      {/if}
      {#if showDeviceEvents && nearbyDeviceEvent}
        <Tooltip.Item
          label={nearbyDeviceEvent.eventType}
          value={nearbyDeviceEvent.notes || ""}
          color={nearbyDeviceEvent.config.color}
          class="font-medium"
        />
      {/if}
      {#if showIob && activeIob}
        <Tooltip.Item
          label="IOB"
          value={activeIob.value}
          format={"decimal"}
          color="var(--iob-basal)"
        />
      {/if}
      {#if showCob && activeCob && activeCob.value > 0}
        <Tooltip.Item
          label="COB"
          value={`${activeCob.value.toFixed(0)}g`}
          color="var(--carbs)"
        />
      {/if}
      {#if showBasal && activeBasal}
        {@const isAdjusted =
          (activeBasal.origin === BasalDeliveryOrigin.Algorithm ||
            activeBasal.origin === BasalDeliveryOrigin.Manual) &&
          activeBasal.rate !== activeBasal.scheduledRate}
        {@const basalLabel =
          activeBasal.origin === BasalDeliveryOrigin.Suspended
            ? "Suspended"
            : isAdjusted
              ? activeBasal.origin === BasalDeliveryOrigin.Algorithm
                ? "Auto Basal"
                : "Temp Basal"
              : "Basal"}
        <Tooltip.Item
          label={basalLabel}
          value={activeBasal.rate}
          format={"decimal"}
          color={isAdjusted || activeBasal.origin === BasalDeliveryOrigin.Suspended
            ? "var(--insulin-temp-basal)"
            : "var(--insulin-basal)"}
          class={cn(
            staleBasalData && data.time >= staleBasalData.start
              ? "text-yellow-500 font-bold"
              : ""
          )}
        />
        {#if isAdjusted && activeBasal.scheduledRate !== undefined}
          <Tooltip.Item
            label="Scheduled"
            value={activeBasal.scheduledRate}
            format={"decimal"}
            color="var(--muted-foreground)"
          />
        {/if}
      {/if}
      {#if showPumpModes && activePumpMode}
        <Tooltip.Item
          label="Pump Mode"
          value={activePumpMode.state}
          color={activePumpMode.color}
          class="font-medium"
        />
      {/if}
      {#if showOverrideSpans && activeOverride}
        <Tooltip.Item
          label="Override"
          value={activeOverride.state}
          color={activeOverride.color}
          class="font-medium"
        />
      {/if}
      {#if showProfileSpans && activeProfile}
        <Tooltip.Item
          label="Profile"
          value={activeProfile.profileName}
          color={activeProfile.color}
        />
      {/if}
      {#if showActivitySpans}
        {#each activeActivities as activity (activity.id)}
          <Tooltip.Item
            label={activity.category}
            value={activity.state}
            color={activity.color}
            class="font-medium"
          />
        {/each}
      {/if}
      {#if showAlarms && nearbySystemEvent}
        <Tooltip.Item
          label={nearbySystemEvent.eventType}
          value={nearbySystemEvent.description || nearbySystemEvent.code || ""}
          color={nearbySystemEvent.color}
          class="font-medium"
        />
      {/if}
    </Tooltip.List>
  {/snippet}
</Tooltip.Root>

<!-- Time axis tooltip -->
<Tooltip.Root
  x="data"
  y={context.height + context.padding.top}
  yOffset={2}
  anchor="top"
  variant="none"
  class="text-sm font-semibold leading-3 px-2 py-1 rounded-sm whitespace-nowrap bg-background"
>
  {#snippet children({ data })}
    <Tooltip.Item
      value={data?.time}
      format="minute"
      onclick={() => goto(`/reports/day-in-review?date=${data?.time}`)}
    />
  {/snippet}
</Tooltip.Root>
