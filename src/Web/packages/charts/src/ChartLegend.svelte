<script lang="ts">
  import { DeviceEventType, PumpModeState, type SystemEventType } from "./enums.js";
  import TextureSwatch from "./print/TextureSwatch.svelte";
  import type { Snippet } from "svelte";
  import { cn } from "@nocturne/ui/utils";
  import { glucoseValue, type GlucoseUnits } from "./format.js";
  import type { ChartLabels } from "./labels.js";
  import type { LegendToggle } from "./options.js";
  import SystemEventIcon from "./icons/SystemEventIcon.svelte";
  import PumpModeIcon from "./icons/PumpModeIcon.svelte";
  import SensorIcon from "./icons/SensorIcon.svelte";
  import SiteChangeIcon from "./icons/SiteChangeIcon.svelte";
  import ReservoirIcon from "./icons/ReservoirIcon.svelte";
  import BatteryIcon from "./icons/BatteryIcon.svelte";
  import BolusIcon from "./icons/BolusIcon.svelte";
  import CarbsIcon from "./icons/CarbsIcon.svelte";
  import Clock from "@lucide/svelte/icons/clock";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import { bgPatternClass } from "./print/patterns.js";

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
    glucoseUnits: GlucoseUnits;
    labels: ChartLabels;
    /** Laid out for paper: event icons print in ink and the paper-only keys show. */
    printing?: boolean;
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

    // Marks keyed only on paper, where there is no tooltip to name them
    hasBgChecks?: boolean;
    /** Hatched basal: inferred from the schedule, or past the pump's last sync. */
    hasUnreportedBasal?: boolean;
    hasScheduledBasal?: boolean;
    targetLow?: number | null;
    targetHigh?: number | null;
    /** Toggles left out, for tracks the host has no data for (e.g. a scope it was not granted). */
    omitted?: readonly LegendToggle[];
  }

  let {
    omitted = [],
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
    hasBgChecks = false,
    hasUnreportedBasal = false,
    hasScheduledBasal = false,
    targetLow = null,
    targetHigh = null,
    glucoseUnits,
    labels,
    printing = false,
  }: Props = $props();

  // Event icons in their own colours print grey; on paper their shape carries them.
  const ink = (color: string) => (printing ? "var(--foreground)" : color);
  const bg = (mgdl: number) => glucoseValue(mgdl, glucoseUnits);
  const pumpModeName = (state: string) =>
    state === PumpModeState.Automatic ? labels.automatic : state;

  const targetLabel = $derived(
    targetLow == null
      ? null
      : targetHigh == null || targetHigh === targetLow
        ? String(bg(targetLow))
        : `${bg(targetLow)}–${bg(targetHigh)}`
  );
</script>

{#snippet legendToggle(
  show: boolean,
  toggle: () => void,
  label: string,
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  children: any
)}
  <!-- eslint-disable-next-line no-restricted-syntax -- chart legend series toggle -->
  <button
    type="button"
    class={cn(
      "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors print:px-0",
      !show && "opacity-50 print:hidden"
    )}
    onclick={toggle}
  >
    {@render children()}
    <span class={cn(!show && "line-through")}>{label}</span>
  </button>
{/snippet}

{#snippet legendIndicator(children: Snippet, label: string)}
  <div class="flex items-center gap-1">
    {@render children()}
    <span>{label}</span>
  </div>
{/snippet}

<!-- Points are coloured by range, which paper in black and white loses, so the
     printed key names each range's bounds for reading a point against the rules. -->
{#snippet glucoseRangeIndicator(colorClass: string, label: string, bounds: string)}
  <div class="flex items-center gap-1">
    <div class="w-2 h-2 rounded-full {colorClass}"></div>
    <span>{label}</span>
    <span class="hidden print:inline">{bounds}</span>
  </div>
{/snippet}

<!-- Icon snippets -->
<!-- Scheduled step, then temp step: the pair the basal track draws. -->
{#snippet basalIcon()}<div class="flex items-end">
    <div class="w-2 h-1.5 bg-insulin-basal"></div>
    <div class="w-2 h-2.5 bg-insulin-temp-basal border border-insulin-bolus"></div>
  </div>{/snippet}
{#snippet iobIcon()}<div
    class="w-3 h-2 bg-iob-basal border border-insulin"
  ></div>{/snippet}
{#snippet cobIcon()}<div
    class="w-3 h-2 bg-carbs/40 border border-carbs {bgPatternClass('carbs')}"
  ></div>{/snippet}
{#snippet bolusIconSnippet()}<BolusIcon size={16} />{/snippet}
{#snippet carbsIconSnippet()}<CarbsIcon size={16} />{/snippet}
{#snippet sensorIcon()}<SensorIcon
    size={16}
    color={ink("var(--glucose-in-range)")}
  />{/snippet}
{#snippet siteIcon()}<SiteChangeIcon
    size={16}
    color={ink("var(--insulin-bolus)")}
  />{/snippet}
{#snippet reservoirIcon()}<ReservoirIcon
    size={16}
    color={ink("var(--insulin-basal)")}
  />{/snippet}
{#snippet batteryIcon()}<BatteryIcon size={16} color={ink("var(--carbs)")} />{/snippet}

<!-- Paper-only keys, for marks the screen names in a tooltip. -->
{#snippet printKey(swatch: Snippet, label: string)}
  <div class="hidden items-center gap-1 print:flex">
    {@render swatch()}
    <span>{label}</span>
  </div>
{/snippet}
{#snippet rangeLimitSwatch()}<TextureSwatch texture="target-range-limit" color="currentColor" shape="line" />{/snippet}
{#snippet targetSwatch()}<TextureSwatch texture="glucose-target" color="currentColor" shape="line" />{/snippet}
{#snippet scheduledBasalSwatch()}<TextureSwatch texture="insulin-scheduled-basal" color="currentColor" shape="line" />{/snippet}
{#snippet bgCheckSwatch()}<svg class="h-3 w-3" viewBox="-8 -8 16 16" aria-hidden="true">
    <polygon points="0,-7 7,0 0,7 -7,0" fill="currentColor" />
  </svg>{/snippet}
{#snippet hatchSwatch()}<TextureSwatch texture="basal-unreported" color="transparent" />{/snippet}
{#snippet overrideIcon()}<div
    class="w-3 h-2 rounded border border-pump-mode-boost bg-pump-mode-boost opacity-30"
  ></div>{/snippet}
{#snippet profileIcon()}<div
    class="w-3 h-2 rounded border border-chart-1 bg-chart-1 opacity-20"
  ></div>{/snippet}
{#snippet activityIcon()}<div class="flex items-center gap-0.5">
    <div
      class="w-2 h-2 rounded bg-pump-mode-sleep"
    ></div>
    <div
      class="w-2 h-2 rounded bg-pump-mode-exercise"
    ></div>
  </div>{/snippet}

<!-- Compact below @md: at a phone's width the full-size legend wraps to three
     rows and pushes the widgets below the fold. -->
<div
  class="flex flex-wrap justify-center gap-x-3 gap-y-1 text-xs @md:gap-4 @md:text-sm text-muted-foreground pt-2"
>
  <!-- Glucose range indicators -->
  {@render glucoseRangeIndicator("bg-glucose-in-range", labels.inRange, `${bg(lowThreshold)}–${bg(highThreshold)}`)}
  {#if glucoseData.some((d) => d.sgv > veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-high", labels.veryHigh, `>${bg(veryHighThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv > highThreshold && d.sgv <= veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-high", labels.high, `>${bg(highThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv < lowThreshold && d.sgv >= veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-low", labels.low, `<${bg(lowThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv < veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-low", labels.veryLow, `<${bg(veryLowThreshold)}`)}
  {/if}
  {@render printKey(rangeLimitSwatch, labels.rangeLimits(String(bg(lowThreshold)), String(bg(highThreshold))))}
  {#if targetLabel}
    {@render printKey(targetSwatch, labels.target(targetLabel))}
  {/if}
  {#if hasBgChecks}
    {@render printKey(bgCheckSwatch, labels.fingerstickBg)}
  {/if}

  <!-- Data toggles -->
  {#if !omitted.includes("basal")}
    {@render legendToggle(showBasal, onToggleBasal, labels.basal, basalIcon)}
  {/if}
  {#if hasScheduledBasal}
    {@render printKey(scheduledBasalSwatch, labels.scheduledBasal)}
  {/if}
  {#if hasUnreportedBasal}
    {@render printKey(hatchSwatch, labels.notReportedByPump)}
  {/if}
  {#if !omitted.includes("iob")}
    {@render legendToggle(showIob, onToggleIob, labels.iob, iobIcon)}
  {/if}
  {#if !omitted.includes("cob")}
    {@render legendToggle(showCob, onToggleCob, labels.cob, cobIcon)}
  {/if}
  {#if !omitted.includes("bolus")}
    {@render legendToggle(showBolus, onToggleBolus, labels.bolus, bolusIconSnippet)}
  {/if}
  {#if !omitted.includes("carbs")}
    {@render legendToggle(showCarbs, onToggleCarbs, labels.carbs, carbsIconSnippet)}
  {/if}

  <!-- Device event legend items (only show if present in current view) -->
  {#if deviceEventMarkers.some((m) => m.eventType === DeviceEventType.SensorStart || m.eventType === DeviceEventType.SensorChange)}
    {@render legendIndicator(sensorIcon, labels.sensor)}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === DeviceEventType.SiteChange)}
    {@render legendIndicator(siteIcon, labels.site)}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === DeviceEventType.InsulinChange)}
    {@render legendIndicator(reservoirIcon, labels.reservoir)}
  {/if}
  {#if deviceEventMarkers.some((m) => m.eventType === DeviceEventType.PumpBatteryChange)}
    {@render legendIndicator(batteryIcon, labels.battery)}
  {/if}

  <!-- Pump mode toggle with expandable dropdown -->
  {#if !omitted.includes("pumpModes")}
  <div class={cn("relative flex items-center", !showPumpModes && "print:hidden")}>
    <!-- eslint-disable-next-line no-restricted-syntax -- chart legend series toggle -->
    <button
      type="button"
      class={cn(
        "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded-l transition-colors print:px-0",
        !showPumpModes && "opacity-50"
      )}
      onclick={onTogglePumpModes}
    >
      <PumpModeIcon
        state={currentPumpMode ?? PumpModeState.Automatic}
        size={14}
        class={showPumpModes ? "opacity-70" : "opacity-40"}
      />
      <span class={cn(!showPumpModes && "line-through")}>
        {pumpModeName(currentPumpMode ?? PumpModeState.Automatic)}
      </span>
    </button>
    {#if uniquePumpModes.length > 1 && showPumpModes}
      <!-- eslint-disable-next-line no-restricted-syntax -- chart legend series toggle -->
      <button
        type="button"
        class="flex items-center cursor-pointer hover:bg-accent/50 px-0.5 py-0.5 rounded-r transition-colors print:hidden"
        onclick={onToggleExpandedPumpModes}
      >
        <ChevronDown
          size={12}
          class={cn("transition-transform", expandedPumpModes && "rotate-180")}
        />
      </button>
    {/if}
    {#if expandedPumpModes && uniquePumpModes.length > 1}
      <div
        class="absolute top-full left-0 mt-1 bg-background border border-border rounded shadow-lg z-50 py-1 min-w-[120px] print:hidden"
      >
        {#each uniquePumpModes as state (state)}
          {@const span = pumpModeSpans.find((s) => s.state === state)}
          {#if span}
            <div
              class="flex items-center gap-2 px-2 py-1 text-xs hover:bg-accent/50"
            >
              <PumpModeIcon state={state ?? ""} size={14} color={ink(span.color ?? "")} />
              <span>{state}</span>
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </div>
  {/if}

  {#if showPumpModes && uniquePumpModes.length > 1 && !omitted.includes("pumpModes")}
    {#each uniquePumpModes.filter((m) => m !== (currentPumpMode ?? PumpModeState.Automatic)) as state (state)}
      {@const span = pumpModeSpans.find((s) => s.state === state)}
      {#if span}
        <div class="hidden items-center gap-1 print:flex">
          <PumpModeIcon state={state ?? ""} size={14} color={ink(span.color ?? "")} />
          <span>{state}</span>
        </div>
      {/if}
    {/each}
  {/if}

  <!-- System event legend items -->
  {#if systemEvents.length > 0}
    {@const uniqueEventTypes = [
      ...new Set(systemEvents.map((e) => e.eventType)),
    ]}
    <!-- eslint-disable-next-line no-restricted-syntax -- chart legend series toggle -->
    <button
      type="button"
      class={cn(
        "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors print:px-0",
        !showAlarms && "opacity-50 print:hidden"
      )}
      onclick={onToggleAlarms}
    >
      {#each uniqueEventTypes.slice(0, 1) as eventType (eventType)}
        {@const event = systemEvents.find((e) => e.eventType === eventType)}
        {#if event && eventType}
          <SystemEventIcon
            {eventType}
            size={14}
            color={showAlarms ? (event.color ?? "var(--muted-foreground)") : "var(--muted-foreground)"}
          />
        {/if}
      {/each}
      <span class={cn(!showAlarms && "line-through")}>
        {labels.alarms(systemEvents.length)}
      </span>
    </button>
  {/if}

  <!-- Scheduled tracker legend items -->
  {#if scheduledTrackerMarkers.length > 0}
    <!-- eslint-disable-next-line no-restricted-syntax -- chart legend series toggle -->
    <button
      type="button"
      class={cn(
        "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors print:px-0",
        !showScheduledTrackers && "opacity-50 print:hidden"
      )}
      onclick={onToggleScheduledTrackers}
    >
      <Clock
        size={14}
        class={showScheduledTrackers
          ? "text-primary"
          : "text-muted-foreground"}
      />
      <span class={cn(!showScheduledTrackers && "line-through")}>
        {labels.scheduledTrackers(scheduledTrackerMarkers.length)}
      </span>
    </button>
  {/if}

  <!-- Override, Profile, Activity spans toggles -->
  {#if !omitted.includes("overrideSpans")}
    {@render legendToggle(showOverrideSpans, onToggleOverrideSpans, labels.overrides, overrideIcon)}
  {/if}
  {#if !omitted.includes("profileSpans")}
    {@render legendToggle(showProfileSpans, onToggleProfileSpans, labels.profile, profileIcon)}
  {/if}
  {#if !omitted.includes("activitySpans")}
    {@render legendToggle(showActivitySpans, onToggleActivitySpans, labels.activity, activityIcon)}
  {/if}
</div>
