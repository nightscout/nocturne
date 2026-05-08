<script lang="ts">
  import { Tooltip, getChartContext } from "layerchart";
  import { cn } from "$lib/utils";
  import { goto } from "$app/navigation";
  import { BasalDeliveryOrigin } from "$lib/api";
  import { bg, bgLabel } from "$lib/utils/formatting";
  import { getGlucoseChartContext } from "./chart-context.svelte";

  interface Props {
    /**
     * Optional extra rows rendered after the built-in tooltip items. Receives
     * the hovered time so callers (e.g. the alert replay simulator) can show
     * events near that instant without forking the tooltip.
     */
    tooltipExtras?: import("svelte").Snippet<[{ time: Date }]>;
  }

  let { tooltipExtras }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  // Finders — bind the generic two-arg finders to their series data
  const findBasal = (time: Date) =>
    ctx.engine.finders.findBasalValue(ctx.engine.basalData, time);
  const findIob = (time: Date) =>
    ctx.engine.finders.findSeriesValue(ctx.engine.iobData, time);
  const findCob = (time: Date) =>
    ctx.engine.finders.findSeriesValue(ctx.engine.cobData, time);

  // Single-arg finders from engine
  const {
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
  } = ctx.engine.finders;

  // Visibility (with defaults for when no legend is set)
  const showBolus = $derived(ctx.legend?.bolus ?? true);
  const showCarbs = $derived(ctx.legend?.carbs ?? true);
  const showDeviceEvents = $derived(ctx.legend?.deviceEvents ?? true);
  const showIob = $derived(ctx.legend?.iob ?? true);
  const showCob = $derived(ctx.legend?.cob ?? true);
  const showBasal = $derived(ctx.legend?.basal ?? true);
  const showPumpModes = $derived(ctx.legend?.pumpModes ?? true);
  const showOverrideSpans = $derived(ctx.legend?.overrideSpans ?? false);
  const showProfileSpans = $derived(ctx.legend?.profileSpans ?? false);
  const showActivitySpans = $derived(ctx.legend?.activitySpans ?? false);
  const showAlarms = $derived(ctx.legend?.alarms ?? true);

  const staleBasalData = $derived(ctx.engine.staleBasalData);
</script>

<Tooltip.Root
  context={chartCtx}
  class="bg-popover/95 border border-border rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
>
  {#snippet children({ data })}
    {@const activeBasal = findBasal(data.time)}
    {@const activeIob = findIob(data.time)}
    {@const activeCob = findCob(data.time)}
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
          color="var(--glucose-in-range)"
          class="text-popover-foreground font-bold"
        />
      {/if}
      {#if showBolus && nearbyBolus}
        <Tooltip.Item
          label="Bolus"
          value={`${(nearbyBolus.insulin ?? 0).toFixed(1)}U`}
          color="var(--insulin-bolus)"
          class="font-medium"
        />
      {/if}
      {#if showCarbs && nearbyCarbs}
        <Tooltip.Item
          label="Carbs"
          value={`${nearbyCarbs.carbs ?? 0}g`}
          color="var(--carbs)"
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
          format="decimal"
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
            format="decimal"
            color={isAdjusted ||
            activeBasal.origin === BasalDeliveryOrigin.Suspended
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
              format="decimal"
              color="var(--muted-foreground)"
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
            format="decimal"
            color={isAdjusted ||
            activeBasalDelivery.origin === BasalDeliveryOrigin.Suspended
              ? "var(--insulin-temp-basal)"
              : "var(--insulin-basal)"}
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
            format="decimal"
            color="var(--insulin-temp-basal)"
          />
          {#if activeTempBasal.percent != null}
            <Tooltip.Item
              label="Percent"
              value={`${activeTempBasal.percent}%`}
              color="var(--muted-foreground)"
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
            label={activity.category ?? ""}
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
      {@render tooltipExtras?.({ time: data.time })}
    </Tooltip.List>
  {/snippet}
</Tooltip.Root>

<!-- Time axis tooltip -->
<Tooltip.Root
  x="data"
  y={chartCtx.height + chartCtx.padding.top}
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
