<script lang="ts">
  import { browser } from "$app/environment";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
  } from "$lib/utils/formatting";
  import { ArrowUp } from "lucide-svelte";
  import GlucoseChartCard from "$lib/components/dashboard/glucose-chart/GlucoseChartCard.svelte";
  import TrackerCategoryIcon from "$lib/components/icons/TrackerCategoryIcon.svelte";
  import type {
    ClockFaceConfig,
    ClockElement,
    ClockElementStyle,
    TrackerDefinitionDto,
  } from "$lib/api/api-client";
  import { getDefinitions } from "$lib/data/generated";

  interface Props {
    config: ClockFaceConfig;
    /** Scale factor for compact previews (default 1 = full size) */
    scale?: number;
    /** Whether to show charts (disable for small previews) */
    showCharts?: boolean;
    /** Additional CSS class for the container */
    class?: string;
  }

  let { config, scale = 1, showCharts = true, class: className = "" }: Props = $props();

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

  // Get BG color based on value
  function getBgColor(bg: number): string {
    if (bg < 70) return "#ef4444";
    if (bg < 80) return "#eab308";
    if (bg > 250) return "#ef4444";
    if (bg > 180) return "#f97316";
    return "#22c55e";
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
    if (!config?.settings) return "background-color: #0a0a0a;";
    if (config.settings.backgroundImage) {
      return `background-image: url(${config.settings.backgroundImage}); background-size: cover; background-position: center;`;
    }
    if (config.settings.bgColor) {
      return `background-color: ${getBgColor(currentBG)};`;
    }
    return "background-color: #0a0a0a;";
  });

  const overlayOpacity = $derived(
    config?.settings?.backgroundImage
      ? (100 - (config.settings.backgroundOpacity ?? 100)) / 100
      : 0
  );
</script>

<div
  class="{className} relative flex flex-col items-center justify-center overflow-hidden"
  style={bgStyle}
>
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
      <div class="absolute inset-0 z-0">
        <GlucoseChartCard
          compact={true}
          heightClass="h-full"
          defaultFocusHours={backgroundChart.hours || 3}
          initialShowIob={backgroundChart.chartConfig?.showIob ?? false}
          initialShowCob={backgroundChart.chartConfig?.showCob ?? false}
          initialShowBasal={backgroundChart.chartConfig?.showBasal ?? false}
          initialShowBolus={backgroundChart.chartConfig?.showBolus ?? true}
          initialShowCarbs={backgroundChart.chartConfig?.showCarbs ?? true}
          initialShowDeviceEvents={backgroundChart.chartConfig?.showDeviceEvents ?? false}
          initialShowAlarms={backgroundChart.chartConfig?.showAlarms ?? false}
          initialShowScheduledTrackers={backgroundChart.chartConfig?.showTrackers ?? false}
          showPredictions={backgroundChart.chartConfig?.showPredictions ?? false}
        />
      </div>
    {:else}
      <!-- Background chart placeholder -->
      <div class="absolute inset-0 z-0 flex items-center justify-center">
        <svg class="h-1/2 w-4/5 opacity-30" viewBox="0 0 100 40" preserveAspectRatio="none">
          <polyline
            fill="none"
            stroke="#22c55e"
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
                <div
                  class="overflow-hidden rounded"
                  style="width: {(element.width || 400) * scale}px; height: {(element.height || 200) * scale}px;"
                >
                  <GlucoseChartCard
                    compact={true}
                    heightClass="h-full"
                    defaultFocusHours={element.hours || 3}
                    initialShowIob={element.chartConfig?.showIob ?? false}
                    initialShowCob={element.chartConfig?.showCob ?? false}
                    initialShowBasal={element.chartConfig?.showBasal ?? false}
                    initialShowBolus={element.chartConfig?.showBolus ?? true}
                    initialShowCarbs={element.chartConfig?.showCarbs ?? true}
                    initialShowDeviceEvents={element.chartConfig?.showDeviceEvents ?? false}
                    initialShowAlarms={element.chartConfig?.showAlarms ?? false}
                    initialShowScheduledTrackers={element.chartConfig?.showTrackers ?? false}
                    showPredictions={element.chartConfig?.showPredictions ?? false}
                  />
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
                      stroke="#22c55e"
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
</div>
