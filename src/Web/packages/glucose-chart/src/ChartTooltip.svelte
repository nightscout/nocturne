<script lang="ts">
  import { Tooltip } from "layerchart";
  import { cn } from "./utils/formatting.js";
  import { BasalDeliveryOrigin, ChartSpanKind } from "./enums.js";
  import { bg, bgLabel } from "./utils/formatting.js";

  // Local types for tooltip data shapes
  interface BasalPoint {
    timestamp?: number;
    rate?: number;
    scheduledRate?: number;
    origin?: BasalDeliveryOrigin;
    fillColor?: string;
    strokeColor?: string;
  }

  interface TimeSeriesPoint {
    time: Date;
    value: number;
  }

  type DisplaySpan<T> = T & { displayStart: Date; displayEnd: Date };

  interface StateSpan {
    id?: string;
    kind?: string;
    category?: string;
    state?: string;
    startTime: Date;
    endTime: Date | null;
    color: string;
  }

  interface BolusMarker {
    time: Date;
    insulin?: number;
  }

  interface CarbMarker {
    time: Date;
    carbs?: number;
  }

  interface DeviceEventMarker {
    time: Date;
    eventType?: string;
    notes?: string | null;
    color: string;
  }

  type ProfileSpan = DisplaySpan<StateSpan> & { profileName: string };

  type TempBasalSpan = DisplaySpan<StateSpan> & {
    rate: number | null;
    percent: number | null;
  };

  interface BasalDeliverySpan {
    startTime: Date;
    endTime: Date | null;
    rate?: number;
    origin?: BasalDeliveryOrigin;
  }

  interface SystemEvent {
    eventType?: string;
    code?: string | null;
    description?: string | null;
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
    findActivePumpMode: (time: Date) => DisplaySpan<StateSpan> | undefined;
    findActiveOverride: (time: Date) => DisplaySpan<StateSpan> | undefined;
    findActiveProfile: (time: Date) => ProfileSpan | undefined;
    findActiveActivities: (time: Date) => DisplaySpan<StateSpan>[];
    findActiveTempBasal: (time: Date) => TempBasalSpan | undefined;
    findActiveBasalDelivery: (
      time: Date
    ) => DisplaySpan<BasalDeliverySpan> | undefined;
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
    findActiveTempBasal,
    findActiveBasalDelivery,
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
    {@const activeTempBasal = findActiveTempBasal(data.time)}
    {@const activeBasalDelivery = findActiveBasalDelivery(data.time)}
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
          value={`${bg(data.sgv)} ${bgLabel()}`}
          color="var(--color-glucose-in-range)"
          class="text-popover-foreground font-bold"
        />
      {/if}
      {#if showBolus && nearbyBolus}
        <Tooltip.Item
          label="Bolus"
          value={`${(nearbyBolus.insulin ?? 0).toFixed(1)}U`}
          color="var(--color-insulin-bolus)"
          class="font-medium"
        />
      {/if}
      {#if showCarbs && nearbyCarbs}
        <Tooltip.Item
          label="Carbs"
          value={`${nearbyCarbs.carbs ?? 0}g`}
          color="var(--color-carbs)"
          class="font-medium"
        />
      {/if}
      {#if showDeviceEvents && nearbyDeviceEvent}
        <Tooltip.Item
          label={nearbyDeviceEvent.eventType}
          value={nearbyDeviceEvent.notes || ""}
          color={nearbyDeviceEvent.color}
          class="font-medium"
        />
      {/if}
      {#if showIob && activeIob}
        <Tooltip.Item
          label="IOB"
          value={activeIob.value}
          format={"decimal"}
          color="var(--color-iob-basal)"
        />
      {/if}
      {#if showCob && activeCob && activeCob.value > 0}
        <Tooltip.Item
          label="COB"
          value={`${activeCob.value.toFixed(0)}g`}
          color="var(--color-carbs)"
        />
      {/if}
      {#if showBasal && (activeBasal || activeBasalDelivery || activeTempBasal)}
        {#if activeBasal}
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
            color={isAdjusted ||
            activeBasal.origin === BasalDeliveryOrigin.Suspended
              ? "var(--color-insulin-temp-basal)"
              : "var(--color-insulin-basal)"}
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
              color="var(--color-muted-foreground)"
            />
          {/if}
        {:else if activeBasalDelivery}
          <!-- Fallback to delivery spans when basalSeries has no data -->
          {@const isAdjusted =
            activeBasalDelivery.origin === BasalDeliveryOrigin.Algorithm ||
            activeBasalDelivery.origin === BasalDeliveryOrigin.Manual}
          {@const basalLabel =
            activeBasalDelivery.origin === BasalDeliveryOrigin.Suspended
              ? "Suspended"
              : activeBasalDelivery.origin === BasalDeliveryOrigin.Algorithm
                ? "Auto Basal"
                : activeBasalDelivery.origin === BasalDeliveryOrigin.Manual
                  ? "Temp Basal"
                  : "Basal"}
          <Tooltip.Item
            label={basalLabel}
            value={activeBasalDelivery.rate ?? 0}
            format={"decimal"}
            color={isAdjusted ||
            activeBasalDelivery.origin === BasalDeliveryOrigin.Suspended
              ? "var(--color-insulin-temp-basal)"
              : "var(--color-insulin-basal)"}
            class={cn(
              staleBasalData && data.time >= staleBasalData.start
                ? "text-yellow-500 font-bold"
                : ""
            )}
          />
        {:else if activeTempBasal && activeTempBasal.rate != null}
          <Tooltip.Item
            label="Temp Basal"
            value={activeTempBasal.rate}
            format={"decimal"}
            color="var(--color-insulin-temp-basal)"
          />
          {#if activeTempBasal.percent != null}
            <Tooltip.Item
              label="Percent"
              value={`${activeTempBasal.percent}%`}
              color="var(--color-muted-foreground)"
            />
          {/if}
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
            label={activity.kind === ChartSpanKind.Sleep
              ? "Sleep"
              : (activity.category ?? "")}
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
    />
  {/snippet}
</Tooltip.Root>
