<script lang="ts">
  import { cn } from "./utils/formatting.js";
  import {
    AlertTriangle,
    Activity,
    Wifi,
    MapPin,
    Droplets,
    Battery,
    Syringe,
    Utensils,
  } from "@lucide/svelte";
  import { SystemEventType } from "./enums.js";
  import { Clock, ChevronDown } from "@lucide/svelte";
  import type { Snippet } from "svelte";
  import LegendButton from "./controls/LegendButton.svelte";

  interface DeviceEventMarker {
    eventType?: string;
  }

  interface SystemEvent {
    id?: string;
    eventType?: SystemEventType;
    color?: string;
  }

  interface PumpModeSpan {
    state?: string;
    color?: string;
  }

  interface ScheduledTrackerMarker {
    id?: string;
  }

  interface GlucoseDataPoint {
    sgv: number;
  }

  interface Props {
    // Glucose range indicators
    glucoseData: GlucoseDataPoint[];
    highThreshold: number;
    lowThreshold: number;
    veryHighThreshold: number;
    veryLowThreshold: number;

    // Toggle states
    showBasal: boolean;
    showIob: boolean;
    showCob: boolean;
    showBolus: boolean;
    showCarbs: boolean;
    showPumpModes: boolean;
    showAlarms: boolean;
    showScheduledTrackers: boolean;
    showOverrideSpans: boolean;
    showProfileSpans: boolean;
    showActivitySpans: boolean;

    // Toggle callbacks
    onToggleBasal: () => void;
    onToggleIob: () => void;
    onToggleCob: () => void;
    onToggleBolus: () => void;
    onToggleCarbs: () => void;
    onTogglePumpModes: () => void;
    onToggleAlarms: () => void;
    onToggleScheduledTrackers: () => void;
    onToggleOverrideSpans: () => void;
    onToggleProfileSpans: () => void;
    onToggleActivitySpans: () => void;

    // Data for conditional rendering
    deviceEventMarkers: DeviceEventMarker[];
    systemEvents: SystemEvent[];
    pumpModeSpans: PumpModeSpan[];
    scheduledTrackerMarkers: ScheduledTrackerMarker[];
    currentPumpMode: string | undefined;
    uniquePumpModes: (string | undefined)[];

    // Pump mode expansion
    expandedPumpModes: boolean;
    onToggleExpandedPumpModes: () => void;
  }

  let {
    glucoseData,
    highThreshold,
    lowThreshold,
    veryHighThreshold,
    veryLowThreshold,
    showBasal,
    showIob,
    showCob,
    showBolus,
    showCarbs,
    showPumpModes,
    showAlarms,
    showScheduledTrackers,
    showOverrideSpans,
    showProfileSpans,
    showActivitySpans,
    onToggleBasal,
    onToggleIob,
    onToggleCob,
    onToggleBolus,
    onToggleCarbs,
    onTogglePumpModes,
    onToggleAlarms,
    onToggleScheduledTrackers,
    onToggleOverrideSpans,
    onToggleProfileSpans,
    onToggleActivitySpans,
    deviceEventMarkers,
    systemEvents,
    pumpModeSpans,
    scheduledTrackerMarkers,
    currentPumpMode,
    uniquePumpModes,
    expandedPumpModes,
    onToggleExpandedPumpModes,
  }: Props = $props();
</script>

{#snippet legendToggle(
  show: boolean,
  toggle: () => void,
  label: string,
  children: Snippet
)}
  <LegendButton pressed={show} onclick={toggle}>
    {@render children()}
    <span class={cn(!show && "line-through")}>{label}</span>
  </LegendButton>
{/snippet}

{#snippet legendIndicator(children: Snippet, label: string)}
  <div class="flex items-center gap-1">
    {@render children()}
    <span>{label}</span>
  </div>
{/snippet}

{#snippet glucoseRangeIndicator(colorClass: string, label: string)}
  <div class="flex items-center gap-1">
    <div class="w-2 h-2 rounded-full {colorClass}"></div>
    <span>{label}</span>
  </div>
{/snippet}

<!-- Icon snippets -->
{#snippet basalIcon()}<div
    class="w-3 h-2 bg-insulin-basal border border-insulin"
  ></div>{/snippet}
{#snippet iobIcon()}<div
    class="w-3 h-2 bg-iob-basal border border-insulin"
  ></div>{/snippet}
{#snippet cobIcon()}<div
    class="w-3 h-2 bg-carbs/40 border border-carbs"
  ></div>{/snippet}
{#snippet bolusIconSnippet()}<Syringe size={16} />{/snippet}
{#snippet carbsIconSnippet()}<Utensils size={16} />{/snippet}
{#snippet sensorIcon()}<Wifi
    size={16}
    color="var(--color-glucose-in-range)"
  />{/snippet}
{#snippet siteIcon()}<MapPin
    size={16}
    color="var(--color-insulin-bolus)"
  />{/snippet}
{#snippet reservoirIcon()}<Droplets
    size={16}
    color="var(--color-insulin-basal)"
  />{/snippet}
{#snippet batteryIcon()}<Battery size={16} color="var(--color-carbs)" />{/snippet}
{#snippet overrideIcon()}<div
    class="w-3 h-2 rounded border bg-pump-mode-boost border-pump-mode-boost opacity-30"
  ></div>{/snippet}
{#snippet profileIcon()}<div
    class="w-3 h-2 rounded border bg-chart-1 border-chart-1 opacity-20"
  ></div>{/snippet}
{#snippet activityIcon()}<div class="flex items-center gap-0.5">
    <div class="w-2 h-2 rounded bg-pump-mode-sleep"></div>
    <div class="w-2 h-2 rounded bg-pump-mode-exercise"></div>
  </div>{/snippet}

<div
  class="flex flex-wrap justify-center gap-4 text-sm text-muted-foreground pt-2"
>
  <!-- Glucose range indicators -->
  {@render glucoseRangeIndicator("bg-glucose-in-range", "In Range")}
  {#if glucoseData.some((d) => d.sgv > veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-high", "Very High")}
  {/if}
  {#if glucoseData.some((d) => d.sgv > highThreshold && d.sgv <= veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-high", "High")}
  {/if}
  {#if glucoseData.some((d) => d.sgv < lowThreshold && d.sgv >= veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-low", "Low")}
  {/if}
  {#if glucoseData.some((d) => d.sgv < veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-low", "Very Low")}
  {/if}

  <!-- Data toggles -->
  {@render legendToggle(showBasal, onToggleBasal, "Basal", basalIcon)}
  {@render legendToggle(showIob, onToggleIob, "IOB", iobIcon)}
  {@render legendToggle(showCob, onToggleCob, "COB", cobIcon)}
  {@render legendToggle(showBolus, onToggleBolus, "Bolus", bolusIconSnippet)}
  {@render legendToggle(showCarbs, onToggleCarbs, "Carbs", carbsIconSnippet)}

  <!-- Device event legend items (only show if present in current view) -->
  {#if deviceEventMarkers.some((m) => m.eventType === "SensorStart" || m.eventType === "SensorChange")}
    {@render legendIndicator(sensorIcon, "Sensor")}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === "SiteChange")}
    {@render legendIndicator(siteIcon, "Site")}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === "InsulinChange")}
    {@render legendIndicator(reservoirIcon, "Reservoir")}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === "PumpBatteryChange")}
    {@render legendIndicator(batteryIcon, "Battery")}
  {/if}

  <!-- Pump mode toggle with expandable dropdown -->
  <div class="relative flex items-center">
    <LegendButton segment="start" pressed={showPumpModes} onclick={onTogglePumpModes}>
      <Activity
        size={14}
        class={showPumpModes ? "opacity-70" : "opacity-40"}
      />
      <span class={cn(!showPumpModes && "line-through")}>
        {currentPumpMode ?? "Automatic"}
      </span>
    </LegendButton>
    {#if uniquePumpModes.length > 1 && showPumpModes}
      <LegendButton segment="end" expanded={expandedPumpModes} onclick={onToggleExpandedPumpModes}>
        <ChevronDown
          size={12}
          class={cn("transition-transform", expandedPumpModes && "rotate-180")}
        />
      </LegendButton>
    {/if}
    {#if expandedPumpModes && uniquePumpModes.length > 1}
      <div
        class="absolute top-full left-0 mt-1 bg-background border border-border rounded shadow-lg z-50 py-1 min-w-[120px]"
      >
        {#each uniquePumpModes as state (state)}
          {@const span = pumpModeSpans.find((s) => s.state === state)}
          {#if span}
            <div
              class="flex items-center gap-2 px-2 py-1 text-xs hover:bg-accent/50"
            >
              <Activity size={14} color={span.color ?? ""} />
              <span>{state}</span>
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </div>

  <!-- System event legend items -->
  {#if systemEvents.length > 0}
    {@const uniqueEventTypes = [
      ...new Set(systemEvents.map((e) => e.eventType)),
    ]}
    <LegendButton pressed={showAlarms} onclick={onToggleAlarms}>
      {#each uniqueEventTypes.slice(0, 1) as eventType (eventType)}
        {@const event = systemEvents.find((e) => e.eventType === eventType)}
        {#if event && eventType}
          <AlertTriangle
            size={14}
            color={showAlarms ? (event.color ?? "var(--color-muted-foreground)") : "var(--color-muted-foreground)"}
          />
        {/if}
      {/each}
      <span class={cn(!showAlarms && "line-through")}>
        Alarms ({systemEvents.length})
      </span>
    </LegendButton>
  {/if}

  <!-- Scheduled tracker legend items -->
  {#if scheduledTrackerMarkers.length > 0}
    <LegendButton pressed={showScheduledTrackers} onclick={onToggleScheduledTrackers}>
      <Clock
        size={14}
        class={showScheduledTrackers
          ? "text-primary"
          : "text-muted-foreground"}
      />
      <span class={cn(!showScheduledTrackers && "line-through")}>
        Scheduled ({scheduledTrackerMarkers.length})
      </span>
    </LegendButton>
  {/if}

  <!-- Override, Profile, Activity spans toggles -->
  {@render legendToggle(
    showOverrideSpans,
    onToggleOverrideSpans,
    "Overrides",
    overrideIcon
  )}
  {@render legendToggle(
    showProfileSpans,
    onToggleProfileSpans,
    "Profile",
    profileIcon
  )}
  {@render legendToggle(
    showActivitySpans,
    onToggleActivitySpans,
    "Activity",
    activityIcon
  )}
</div>
