<script lang="ts">
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import type { Snippet } from "svelte";
  import { cn } from "$lib/utils";
  import { bg } from "$lib/utils/formatting";
  import {
    SystemEventIcon,
    PumpModeIcon,
    SensorIcon,
    SiteChangeIcon,
    ReservoirIcon,
    BatteryIcon,
    BolusIcon,
    CarbsIcon,
  } from "$lib/components/icons";
  import { SystemEventType } from "$lib/api";
  import Clock from "lucide-svelte/icons/clock";
  import ChevronDown from "lucide-svelte/icons/chevron-down";
  import { bgPatternClass } from "$lib/components/charts/print/chart-print-patterns";
  import { PrintMode } from "$lib/components/charts/print/print-mode.svelte";

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

    // Marks keyed only on paper, where there is no tooltip to name them
    hasBgChecks?: boolean;
    /** Hatched basal: inferred from the schedule, or past the pump's last sync. */
    hasUnreportedBasal?: boolean;
    hasScheduledBasal?: boolean;
    targetLow?: number | null;
    targetHigh?: number | null;
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
    hasBgChecks = false,
    hasUnreportedBasal = false,
    hasScheduledBasal = false,
    targetLow = null,
    targetHigh = null,
  }: Props = $props();

  // Event icons in their own colours print grey; on paper their shape carries them.
  const print = new PrintMode();
  const ink = (color: string) => (print.active ? "var(--foreground)" : color);

  const targetLabel = $derived(
    targetLow == null
      ? null
      : targetHigh == null || targetHigh === targetLow
        ? bg(targetLow)
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
    class="w-3 h-2 rounded border border-(--pump-mode-boost) bg-(--pump-mode-boost) opacity-30"
  ></div>{/snippet}
{#snippet profileIcon()}<div
    class="w-3 h-2 rounded border border-chart-1 bg-chart-1 opacity-20"
  ></div>{/snippet}
{#snippet activityIcon()}<div class="flex items-center gap-0.5">
    <div
      class="w-2 h-2 rounded bg-(--pump-mode-sleep)"
    ></div>
    <div
      class="w-2 h-2 rounded bg-(--pump-mode-exercise)"
    ></div>
  </div>{/snippet}

<!-- Compact below @md: at a phone's width the full-size legend wraps to three
     rows and pushes the widgets below the fold. -->
<div
  class="flex flex-wrap justify-center gap-x-3 gap-y-1 text-xs @md:gap-4 @md:text-sm text-muted-foreground pt-2"
>
  <!-- Glucose range indicators -->
  {@render glucoseRangeIndicator("bg-glucose-in-range", "In Range", `${bg(lowThreshold)}–${bg(highThreshold)}`)}
  {#if glucoseData.some((d) => d.sgv > veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-high", "Very High", `>${bg(veryHighThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv > highThreshold && d.sgv <= veryHighThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-high", "High", `>${bg(highThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv < lowThreshold && d.sgv >= veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-low", "Low", `<${bg(lowThreshold)}`)}
  {/if}
  {#if glucoseData.some((d) => d.sgv < veryLowThreshold)}
    {@render glucoseRangeIndicator("bg-glucose-very-low", "Very Low", `<${bg(veryLowThreshold)}`)}
  {/if}
  {@render printKey(rangeLimitSwatch, `Range limits ${bg(lowThreshold)} / ${bg(highThreshold)}`)}
  {#if targetLabel}
    {@render printKey(targetSwatch, `Target ${targetLabel}`)}
  {/if}
  {#if hasBgChecks}
    {@render printKey(bgCheckSwatch, "Fingerstick BG")}
  {/if}

  <!-- Data toggles -->
  {@render legendToggle(showBasal, onToggleBasal, "Basal", basalIcon)}
  {#if hasScheduledBasal}
    {@render printKey(scheduledBasalSwatch, "Scheduled basal")}
  {/if}
  {#if hasUnreportedBasal}
    {@render printKey(hatchSwatch, "Not reported by pump")}
  {/if}
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
        state={currentPumpMode ?? "Automatic"}
        size={14}
        class={showPumpModes ? "opacity-70" : "opacity-40"}
      />
      <span class={cn(!showPumpModes && "line-through")}>
        {currentPumpMode ?? "Automatic"}
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

  {#if showPumpModes && uniquePumpModes.length > 1}
    {#each uniquePumpModes.filter((m) => m !== (currentPumpMode ?? "Automatic")) as state (state)}
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
        Alarms ({systemEvents.length})
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
        Scheduled ({scheduledTrackerMarkers.length})
      </span>
    </button>
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
