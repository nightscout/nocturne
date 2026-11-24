<script lang="ts">
  import { Tooltip, getChartContext } from "layerchart";
  import { cn } from "@nocturne/ui/utils";
  import { BasalDeliveryOrigin, ChartSpanKind } from "./enums.js";
  import {
    formatCarbDisplay,
    formatClockTime,
    formatInsulinDisplay,
    glucoseUnitLabel,
    glucoseValue,
  } from "./format.js";
  import { getGlucoseChartContext } from "./context.svelte.js";
  import { isBasalAdjusted } from "./view/basal-presentation.js";
  import type { GlucosePoint } from "./view/chart-data-view.svelte.js";

  interface Props {
    /**
     * Optional extra rows rendered after the built-in tooltip items. Receives
     * the hovered time so callers (e.g. the alert replay simulator) can show
     * events near that instant without forking the tooltip.
     */
    tooltipExtras?: import("svelte").Snippet<[{ time: Date }]>;
    /** Makes the time label a link; the app passes `openDayInReview`. */
    onTimeClick?: (time: Date | undefined) => void;
  }

  let { tooltipExtras, onTimeClick }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext<GlucosePoint>();

  // Finders — bind the generic two-arg finders to their series data
  const findBasal = (time: Date) =>
    ctx.view.finders.findBasalValue(ctx.view.basalData, time);
  const findIob = (time: Date) =>
    ctx.view.finders.findSeriesValue(ctx.view.iobData, time);
  const findCob = (time: Date) =>
    ctx.view.finders.findSeriesValue(ctx.view.cobData, time);

  // Single-arg finders from the view
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
  } = ctx.view.finders;

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

  const staleBasalData = $derived(ctx.view.staleBasalData);
  const labels = $derived(ctx.labels);
  const time = (v: unknown) => (v instanceof Date ? formatClockTime(v, ctx.format) : String(v));
</script>

<Tooltip.Root
  context={chartCtx}
  class="border border-border rounded-lg shadow-xl text-xs z-50 print:hidden"
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
      format={time}
      class="text-popover-foreground border-b border-border pb-1 mb-1 text-sm font-semibold"
    />
    <Tooltip.List>
      {#if data?.sgv}
        <Tooltip.Item
          label={labels.glucose}
          value={`${glucoseValue(data.sgv, ctx.glucoseUnits)} ${glucoseUnitLabel(ctx.glucoseUnits)}`}
          color="var(--glucose-in-range)"
          class="text-popover-foreground font-bold"
        />
      {/if}
      {#if showBolus && nearbyBolus}
        <Tooltip.Item
          label={labels.bolus}
          value={labels.insulinAmount(formatInsulinDisplay(nearbyBolus.insulin ?? 0))}
          color="var(--insulin-bolus)"
          class="font-medium"
        />
      {/if}
      {#if showCarbs && nearbyCarbs}
        <Tooltip.Item
          label={labels.carbs}
          value={labels.carbAmount(formatCarbDisplay(nearbyCarbs.carbs ?? 0))}
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
          label={labels.iob}
          value={activeIob.value}
          format="decimal"
          color="var(--iob-basal)"
        />
      {/if}
      {#if showCob && activeCob && activeCob.value > 0}
        <Tooltip.Item
          label={labels.cob}
          value={labels.carbAmount(activeCob.value.toFixed(0))}
          color="var(--carbs)"
        />
      {/if}
      {#if showBasal && (activeBasal || activeBasalDelivery || activeTempBasal)}
        {#if activeBasal}
          {@const isAdjusted = isBasalAdjusted(
            activeBasal.origin,
            activeBasal.rate,
            activeBasal.scheduledRate
          )}
          {@const basalLabel =
            activeBasal.origin === BasalDeliveryOrigin.Suspended
              ? labels.suspended
              : isAdjusted
                ? activeBasal.origin === BasalDeliveryOrigin.Algorithm
                  ? labels.autoBasal
                  : labels.tempBasal
                : labels.basal}
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
                ? "text-warning font-bold"
                : ""
            )}
          />
          {#if isAdjusted && activeBasal.scheduledRate != null}
            <Tooltip.Item
              label={labels.scheduled}
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
              ? labels.suspended
              : activeBasalDelivery.origin === BasalDeliveryOrigin.Algorithm
                ? labels.autoBasal
                : activeBasalDelivery.origin === BasalDeliveryOrigin.Manual
                  ? labels.tempBasal
                  : labels.basal}
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
                ? "text-warning font-bold"
                : ""
            )}
          />
        {:else if activeTempBasal && activeTempBasal.rate != null}
          <Tooltip.Item
            label={labels.tempBasal}
            value={activeTempBasal.rate}
            format="decimal"
            color="var(--insulin-temp-basal)"
          />
          {#if activeTempBasal.percent != null}
            <Tooltip.Item
              label={labels.percent}
              value={`${activeTempBasal.percent}%`}
              color="var(--muted-foreground)"
            />
          {/if}
        {/if}
      {/if}
      {#if showPumpModes && activePumpMode}
        <Tooltip.Item
          label={labels.pumpMode}
          value={activePumpMode.state}
          color={activePumpMode.color}
          class="font-medium"
        />
      {/if}
      {#if showOverrideSpans && activeOverride}
        <Tooltip.Item
          label={labels.override}
          value={activeOverride.state}
          color={activeOverride.color}
          class="font-medium"
        />
      {/if}
      {#if showProfileSpans && activeProfile}
        <Tooltip.Item
          label={labels.profile}
          value={activeProfile.profileName}
          color={activeProfile.color}
        />
      {/if}
      {#if showActivitySpans}
        {#each activeActivities as activity (activity.id)}
          <Tooltip.Item
            label={activity.kind === ChartSpanKind.Sleep
              ? labels.sleep
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
  class="text-sm font-semibold leading-3 px-2 py-1 rounded-sm whitespace-nowrap bg-background print:hidden"
>
  {#snippet children({ data })}
    <Tooltip.Item
      value={data?.time}
      format={time}
      onclick={onTimeClick && (() => onTimeClick(data?.time))}
    />
  {/snippet}
</Tooltip.Root>
