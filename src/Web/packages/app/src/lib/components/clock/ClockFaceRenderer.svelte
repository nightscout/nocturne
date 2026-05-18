<script lang="ts">
  import { browser } from "$app/environment";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
  } from "$lib/utils/formatting";
  import { ArrowUp } from "lucide-svelte";
  import { createChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte";
  import GlucoseChartShell from "$lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import GlucoseTrack from "$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import BasalTrack from "$lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte";
  import IobCobTrack from "$lib/components/dashboard/glucose-chart/tracks/IobCobTrack.svelte";
  import ThresholdRules from "$lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";
  import PredictionTrack from "$lib/components/dashboard/glucose-chart/tracks/PredictionTrack.svelte";
  import DeviceEventMarkers from "$lib/components/dashboard/glucose-chart/markers/DeviceEventMarkers.svelte";
  import TrackerMarkers from "$lib/components/dashboard/glucose-chart/markers/TrackerMarkers.svelte";
  import ChartTooltip from "$lib/components/dashboard/glucose-chart/ChartTooltip.svelte";
  import TrackerCategoryIcon from "$lib/components/icons/TrackerCategoryIcon.svelte";
  import type {
    ClockFaceConfig,
    ClockElement,
    ClockElementStyle,
    TrackerDefinitionDto,
  } from "$lib/api";
  import { getDefinitions } from "$api/generated/trackers.generated.remote";
  import {
    advance,
    angleToVel,
    computeAngleToCorner,
    randomNonAxialAngle,
    type Vec2,
  } from "$lib/components/clock/screensaver-math";
  import ScreensaverPulse, { PULSE_DURATION_MS } from "$lib/components/clock/ScreensaverPulse.svelte";

  interface Props {
    config: ClockFaceConfig;
    /** Scale factor for compact previews (default 1 = full size) */
    scale?: number;
    /** Whether to show charts (disable for small previews) */
    showCharts?: boolean;
    /** Additional CSS class for the container */
    class?: string;
    /** Enable bouncing screensaver mode. Only honour from fullscreen views. */
    screensaver?: boolean;
  }

  let {
    config,
    scale = 1,
    showCharts = true,
    class: className = "",
    screensaver = false,
  }: Props = $props();

  const realtimeStore = getRealtimeStore();

  // Get current glucose values from realtime store
  const currentBG = $derived(realtimeStore.currentBG);
  const bgDelta = $derived(realtimeStore.bgDelta);
  const direction = $derived(realtimeStore.direction);
  const lastUpdated = $derived(realtimeStore.lastUpdated);

  // Format for display based on user's unit preference
  const units = $derived(glucoseUnits.current);
  const displayBG = $derived(formatGlucoseValue(currentBG, units));
  const displayDelta = $derived(formatGlucoseDelta(bgDelta, units));

  // Calculate staleness
  const isStale = $derived.by(() => {
    if (!config?.settings?.staleMinutes) return false;
    if (config.settings.staleMinutes === 0) return false;
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    return mins >= config.settings.staleMinutes;
  });

  // Time since last reading
  const timeSince = $derived.by(() => {
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return "now";
    return `${mins}m`;
  });

  // Current time state
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  // Format time based on 12h/24h preference
  function formatTime(format: string | undefined): string {
    const is24h = format === "24h";
    return currentTime.toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
      hour12: !is24h,
    });
  }

  // Resolve CSS variable to its computed value
  function resolveCssVar(name: string): string {
    if (!browser) return "#000000"; // fallback for SSR
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  // Get BG color based on value
  function getBgColor(bg: number): string {
    if (bg < 70) return resolveCssVar("--glucose-very-low");
    if (bg < 80) return resolveCssVar("--glucose-low");
    if (bg > 250) return resolveCssVar("--glucose-very-high");
    if (bg > 180) return resolveCssVar("--glucose-high");
    return resolveCssVar("--glucose-in-range");
  }

  // Get rotation degrees for Lucide arrow based on direction
  function getDirectionRotation(dir: string): number {
    const rotations: Record<string, number> = {
      DoubleUp: 0,
      SingleUp: 0,
      FortyFiveUp: 45,
      Flat: 90,
      FortyFiveDown: 135,
      SingleDown: 180,
      DoubleDown: 180,
    };
    return rotations[dir] ?? 90;
  }

  // Check if direction is double arrow
  function isDoubleArrow(dir: string): boolean {
    return dir === "DoubleUp" || dir === "DoubleDown";
  }

  // Tracker definitions
  let trackerDefinitions = $state<TrackerDefinitionDto[]>([]);
  $effect(() => {
    if (browser) {
      getDefinitions({})
        .then((defs) => {
          trackerDefinitions = defs;
        })
        .catch(() => {
          trackerDefinitions = [];
        });
    }
  });

  // Get tracker definition by ID
  function getTrackerDefinition(definitionId: string | undefined) {
    if (!definitionId) return null;
    return trackerDefinitions.find((d) => d.id === definitionId) ?? null;
  }

  // Font class helpers
  function getFontClass(font: string | undefined): string {
    switch (font) {
      case "mono":
        return "font-mono";
      case "serif":
        return "font-serif";
      case "sans":
        return "font-sans";
      default:
        return "";
    }
  }

  function getFontWeightClass(weight: string | undefined): string {
    switch (weight) {
      case "normal":
        return "font-normal";
      case "medium":
        return "font-medium";
      case "semibold":
        return "font-semibold";
      case "bold":
        return "font-bold";
      default:
        return "font-medium";
    }
  }

  // Get element color
  function getElementColor(style: ClockElementStyle | undefined): string {
    const color = style?.color;
    if (color === "dynamic") return getBgColor(currentBG);
    return color || "#ffffff";
  }

  // Build custom CSS properties string from element.style.custom
  function buildCustomCssString(element: ClockElement): string {
    const custom = element.style?.custom;
    if (!custom) return "";
    return Object.entries(custom)
      .map(([key, value]) => `${key}: ${value}`)
      .join("; ");
  }

  // Build inline style string
  function buildStyleString(element: ClockElement): string {
    const style = element.style;
    const parts: string[] = [];
    const size = (element.size || 20) * scale;
    parts.push(`font-size: ${size}px`);
    parts.push(`color: ${getElementColor(style)}`);
    parts.push(`opacity: ${style?.opacity ?? 1.0}`);
    const customCss = buildCustomCssString(element);
    if (customCss) {
      parts.push(customCss);
    }
    return parts.join("; ");
  }

  // Render element value (for text-based elements, not arrow/tracker)
  function renderElementValue(element: ClockElement): string {
    switch (element.type) {
      case "sg":
        return String(displayBG);
      case "delta":
        return `${bgDelta > 0 ? "+" : ""}${displayDelta}${element.showUnits !== false ? "" : ""}`;
      case "arrow":
        return ""; // Handled separately with Lucide icon
      case "age":
        return `${timeSince} ago`;
      case "time":
        return formatTime(element.format);
      case "iob":
        return "--U";
      case "cob":
        return "--g";
      case "basal":
        return "0.8U/h";
      case "forecast":
        return `${currentBG + 10}`;
      case "tracker":
        return ""; // Handled separately with icon + time
      case "text":
        return element.text || "";
      default:
        return "";
    }
  }

  // Background chart element
  const backgroundChart = $derived.by(() => {
    if (!config?.rows) return null;
    for (const row of config.rows) {
      for (const element of row.elements ?? []) {
        if (element.type === "chart" && element.chartConfig?.asBackground) {
          return element;
        }
      }
    }
    return null;
  });

  // Preview background style
  const bgStyle = $derived.by(() => {
    if (!config?.settings) return "background-color: var(--background);";
    if (config.settings.backgroundImage) {
      return `background-image: url(${config.settings.backgroundImage}); background-size: cover; background-position: center;`;
    }
    if (config.settings.bgColor) {
      return `background-color: ${getBgColor(currentBG)};`;
    }
    return "background-color: var(--background);";
  });

  const overlayOpacity = $derived(
    config?.settings?.backgroundImage
      ? (100 - (config.settings.backgroundOpacity ?? 100)) / 100
      : 0
  );

  // Screensaver bouncing state
  const SCREENSAVER_SPEED = 60; // px/sec
  const CORNER_HIT_MIN_MS = 10 * 60 * 1000;
  const CORNER_HIT_MAX_MS = 20 * 60 * 1000;
  const CORNER_ARM_LEAD_MS = 30 * 1000;

  let bouncerRef: HTMLDivElement | null = $state(null);
  let blockSize = $state({ w: 0, h: 0 });
  let viewportSize = $state({ w: 0, h: 0 });
  let pos = $state<Vec2>({ x: 0, y: 0 });
  let vel = $state<Vec2>({ x: 0, y: 0 });
  let pulses = $state<{ id: number; x: number; y: number }[]>([]);
  let pulseSeq = 0;

  let nextCornerHitAt = 0;
  let armedForCorner = false;

  function scheduleNextCornerHit() {
    const span = CORNER_HIT_MAX_MS - CORNER_HIT_MIN_MS;
    nextCornerHitAt = Date.now() + CORNER_HIT_MIN_MS + Math.random() * span;
    armedForCorner = false;
  }

  function emitPulse(x: number, y: number) {
    const id = ++pulseSeq;
    pulses = [...pulses, { id, x, y }];
    setTimeout(() => {
      pulses = pulses.filter((p) => p.id !== id);
    }, PULSE_DURATION_MS + 100);
  }

  function pickCorner(): Vec2 {
    const maxX = Math.max(0, viewportSize.w - blockSize.w);
    const maxY = Math.max(0, viewportSize.h - blockSize.h);
    const corners: Vec2[] = [
      { x: 0, y: 0 },
      { x: maxX, y: 0 },
      { x: 0, y: maxY },
      { x: maxX, y: maxY },
    ];
    return corners[Math.floor(Math.random() * corners.length)];
  }

  $effect(() => {
    if (!browser || !screensaver || !bouncerRef) return;
    const ro = new ResizeObserver((entries) => {
      const e = entries[0];
      if (!e) return;
      blockSize = { w: e.contentRect.width, h: e.contentRect.height };
    });
    ro.observe(bouncerRef);
    return () => ro.disconnect();
  });

  $effect(() => {
    if (!browser || !screensaver) return;

    const updateViewport = () => {
      viewportSize = { w: window.innerWidth, h: window.innerHeight };
    };
    updateViewport();
    window.addEventListener("resize", updateViewport);

    const angle = randomNonAxialAngle(Math.random);
    vel = angleToVel(angle, SCREENSAVER_SPEED);
    scheduleNextCornerHit();

    let raf = 0;
    let lastT = 0;
    let positioned = false;

    const tick = (t: number) => {
      if (document.visibilityState !== "visible") {
        lastT = 0;
        raf = requestAnimationFrame(tick);
        return;
      }
      if (blockSize.w <= 0 || blockSize.h <= 0) {
        lastT = 0;
        raf = requestAnimationFrame(tick);
        return;
      }
      if (!positioned) {
        pos = {
          x: Math.random() * Math.max(0, viewportSize.w - blockSize.w),
          y: Math.random() * Math.max(0, viewportSize.h - blockSize.h),
        };
        positioned = true;
      }
      if (lastT === 0) lastT = t;
      const dt = Math.min(0.05, (t - lastT) / 1000);
      lastT = t;

      const now = Date.now();
      if (!armedForCorner && now >= nextCornerHitAt - CORNER_ARM_LEAD_MS) {
        armedForCorner = true;
      }

      const result = advance(
        pos,
        vel,
        {
          blockW: blockSize.w,
          blockH: blockSize.h,
          viewportW: viewportSize.w,
          viewportH: viewportSize.h,
        },
        dt
      );

      pos = result.pos;
      vel = result.vel;

      const hitX = result.hitLeft || result.hitRight;
      const hitY = result.hitTop || result.hitBottom;

      if (armedForCorner && (hitX || hitY) && !(hitX && hitY)) {
        // Just bounced off one wall. Steer the new trajectory to a corner
        // from the post-bounce position so the direction change is hidden
        // inside the bounce.
        const target = pickCorner();
        vel = computeAngleToCorner(pos, target, SCREENSAVER_SPEED);
      }

      if (hitX && hitY) {
        const cx = result.hitLeft ? 0 : viewportSize.w;
        const cy = result.hitTop ? 0 : viewportSize.h;
        emitPulse(cx, cy);
        if (armedForCorner) scheduleNextCornerHit();
      }

      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener("resize", updateViewport);
    };
  });
</script>

{#snippet body()}
  <!-- Background overlay for image opacity -->
  {#if config?.settings?.backgroundImage}
    <div
      class="absolute inset-0 bg-black"
      style="opacity: {overlayOpacity}"
    ></div>
  {/if}

  <!-- Background chart -->
  {#if backgroundChart}
    {#if showCharts}
      {@const bgChartEngine = createChartDataEngine({
        focusHours: backgroundChart.hours || 3,
        enablePredictions: backgroundChart.chartConfig?.showPredictions ?? false,
      })}
      <div class="absolute inset-0 z-0">
        <GlucoseChartShell engine={bgChartEngine} heightClass="h-full">
          {#snippet tracks(_ctx)}
            {#if backgroundChart.chartConfig?.showBasal ?? false}
              <BasalTrack />
            {/if}
            <ThresholdRules />
            <GlucoseTrack />
            {#if backgroundChart.chartConfig?.showPredictions ?? false}
              <PredictionTrack />
            {/if}
            {#if (backgroundChart.chartConfig?.showBolus ?? true) || (backgroundChart.chartConfig?.showCarbs ?? true) || (backgroundChart.chartConfig?.showIob ?? false) || (backgroundChart.chartConfig?.showCob ?? false)}
              <IobCobTrack />
            {/if}
            {#if backgroundChart.chartConfig?.showDeviceEvents ?? false}
              <DeviceEventMarkers />
            {/if}
            {#if backgroundChart.chartConfig?.showTrackers ?? false}
              <TrackerMarkers />
            {/if}
          {/snippet}
          {#snippet overlays(_ctx)}
            <ChartTooltip />
          {/snippet}
        </GlucoseChartShell>
      </div>
    {:else}
      <!-- Background chart placeholder -->
      <div class="absolute inset-0 z-0 flex items-center justify-center">
        <svg class="h-1/2 w-4/5 opacity-30" viewBox="0 0 100 40" preserveAspectRatio="none">
          <polyline
            fill="none"
            stroke="var(--glucose-in-range)"
            stroke-width="1.5"
            stroke-linecap="round"
            stroke-linejoin="round"
            points="0,25 10,23 20,20 30,22 40,18 50,15 60,17 70,14 80,16 90,12 100,15"
          />
        </svg>
      </div>
    {/if}
  {/if}

  <!-- Rows -->
  <div
    class="relative z-10 flex flex-col items-center p-2"
    style="gap: {3 * scale}px;"
  >
    {#each config?.rows ?? [] as row}
      <div class="flex items-center" style="gap: {2 * scale}px;">
        {#each row.elements ?? [] as element}
          {#if !(element.type === "chart" && element.chartConfig?.asBackground)}
            {#if element.type === "chart"}
              {#if showCharts}
                {@const inlineEngine = createChartDataEngine({
                  focusHours: element.hours || 3,
                  enablePredictions: element.chartConfig?.showPredictions ?? false,
                })}
                <div
                  class="overflow-hidden rounded"
                  style="width: {(element.width || 400) * scale}px; height: {(element.height || 200) * scale}px;"
                >
                  <GlucoseChartShell engine={inlineEngine} heightClass="h-full">
                    {#snippet tracks(_ctx)}
                      {#if element.chartConfig?.showBasal ?? false}
                        <BasalTrack />
                      {/if}
                      <ThresholdRules />
                      <GlucoseTrack />
                      {#if element.chartConfig?.showPredictions ?? false}
                        <PredictionTrack />
                      {/if}
                      {#if (element.chartConfig?.showBolus ?? true) || (element.chartConfig?.showCarbs ?? true) || (element.chartConfig?.showIob ?? false) || (element.chartConfig?.showCob ?? false)}
                        <IobCobTrack />
                      {/if}
                      {#if element.chartConfig?.showDeviceEvents ?? false}
                        <DeviceEventMarkers />
                      {/if}
                      {#if element.chartConfig?.showTrackers ?? false}
                        <TrackerMarkers />
                      {/if}
                    {/snippet}
                    {#snippet overlays(_ctx)}
                      <ChartTooltip />
                    {/snippet}
                  </GlucoseChartShell>
                </div>
              {:else}
                <!-- Inline chart placeholder -->
                <div
                  class="flex items-center justify-center overflow-hidden rounded border border-white/10 bg-white/5"
                  style="width: {(element.width || 400) * scale}px; height: {(element.height || 200) * scale}px;"
                >
                  <svg class="h-3/4 w-4/5 opacity-40" viewBox="0 0 100 40" preserveAspectRatio="none">
                    <polyline
                      fill="none"
                      stroke="var(--glucose-in-range)"
                      stroke-width="1.5"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      points="0,25 10,23 20,20 30,22 40,18 50,15 60,17 70,14 80,16 90,12 100,15"
                    />
                  </svg>
                </div>
              {/if}
            {:else if element.type === "arrow"}
              <!-- Arrow element using Lucide icon with rotation -->
              {@const size = (element.size || 25) * scale}
              {@const rotation = getDirectionRotation(direction)}
              {@const isDouble = isDoubleArrow(direction)}
              {@const customCss = buildCustomCssString(element)}
              <div
                class="flex items-center"
                style="color: {getElementColor(element.style)}; opacity: {element.style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
              >
                {#if isDouble}
                  <ArrowUp
                    style="width: {size}px; height: {size}px; transform: rotate({rotation}deg); margin-right: -{size * 0.3}px;"
                  />
                {/if}
                <ArrowUp
                  style="width: {size}px; height: {size}px; transform: rotate({rotation}deg);"
                />
              </div>
            {:else if element.type === "tracker"}
              <!-- Tracker element with icon and time remaining -->
              {@const def = getTrackerDefinition(element.definitionId)}
              {@const size = (element.size || 14) * scale}
              {@const showOptions = element.show ?? ["name", "remaining"]}
              {@const customCss = buildCustomCssString(element)}
              <div
                class="flex items-center gap-1 {getFontClass(element.style?.font)} {getFontWeightClass(element.style?.fontWeight)}"
                style="color: {getElementColor(element.style)}; opacity: {element.style?.opacity ?? 1.0}; font-size: {size}px;{customCss ? ` ${customCss}` : ''}"
              >
                {#if showOptions.includes("icon") && def?.category}
                  <TrackerCategoryIcon
                    category={def.category}
                    class="shrink-0"
                    style="width: {size * 1.2}px; height: {size * 1.2}px;"
                  />
                {/if}
                {#if showOptions.includes("name")}
                  <span class="leading-none">{def?.name ?? "Tracker"}</span>
                {/if}
                {#if showOptions.includes("remaining")}
                  <span class="leading-none tabular-nums opacity-70">2d 4h</span>
                {/if}
              </div>
            {:else if element.type !== "chart"}
              <span
                class="leading-none tabular-nums {getFontClass(element.style?.font)} {getFontWeightClass(element.style?.fontWeight)} {isStale && element.type === 'sg' ? 'line-through opacity-60' : ''}"
                style={buildStyleString(element)}
              >
                {renderElementValue(element)}
              </span>
            {/if}
          {/if}
        {/each}
      </div>
    {/each}
  </div>
{/snippet}

{#if screensaver}
  <div class="{className} fixed inset-0 overflow-hidden bg-black">
    <div
      bind:this={bouncerRef}
      class="absolute"
      style="transform: translate3d({pos.x}px, {pos.y}px, 0); will-change: transform;"
    >
      <div
        class="relative flex flex-col items-center justify-center overflow-hidden"
        style={bgStyle}
      >
        {@render body()}
      </div>
    </div>
    {#each pulses as p (p.id)}
      <ScreensaverPulse x={p.x} y={p.y} />
    {/each}
  </div>
{:else}
  <div
    class="{className} relative flex flex-col items-center justify-center overflow-hidden"
    style={bgStyle}
  >
    {@render body()}
  </div>
{/if}
