<script lang="ts">
  import { browser } from "$app/env";
  import { getClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
  import {
    buildCustomCssString,
    buildStyleString,
    clockBackgroundStyle,
    elementSize,
    getElementColor,
    getFontClass,
    getFontWeightClass,
  } from "#lib/clock-builder/index.js";
  import { faceUnits, renderClockElementValue } from "#lib/components/clock/element-value.js";
  import TrendArrow from "#lib/components/clock/TrendArrow.svelte";
  import ClockChart from "#lib/components/clock/ClockChart.svelte";
  import ClockTrackers from "#lib/components/clock/ClockTrackers.svelte";
  import { getClockFaceDataSource, visibleTrackers } from "#lib/components/clock/clock-face-data.svelte.js";
  import type { ClockFaceConfig } from "#lib/api/index.js";
  import {
    advance,
    angleToVel,
    computeAngleToCorner,
    randomNonAxialAngle,
    type Vec2,
  } from "#lib/components/clock/screensaver-math.js";
  import ScreensaverPulse, { PULSE_DURATION_MS } from "#lib/components/clock/ScreensaverPulse.svelte";
  import { isClockReadingStale } from "#lib/components/clock/staleness.js";

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

  // Live glucose source: the realtime store in authenticated previews, or the
  // polling PublicClockStore on an anonymous public clock link.
  const glucose = getClockGlucoseSource();
  // Everything else the elements show: IOB, COB, basal, forecast, time in range, trackers, charts.
  const faceData = getClockFaceDataSource();

  const currentBG = $derived(glucose.currentBG);
  const direction = $derived(glucose.direction);
  const lastUpdated = $derived(glucose.lastUpdated);

  // Current time state
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  const isStale = $derived(
    isClockReadingStale(
      config?.settings?.staleMinutes,
      lastUpdated,
      currentTime.getTime()
    )
  );

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

  const bgStyle = $derived(
    clockBackgroundStyle(config?.settings, currentBG, "var(--background)", glucose.thresholds)
  );

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
    <div class="absolute inset-0 z-0">
      {#if showCharts}
        <ClockChart
          chartConfig={backgroundChart.chartConfig}
          hours={backgroundChart.hours}
          glucoseUnits={faceUnits(config?.settings)}
        />
      {/if}
    </div>
  {/if}

  <!-- Rows -->
  <div
    data-testid="clock-face-rows"
    class="relative z-10 flex flex-col items-center p-2"
    style="gap: {3 * scale}px;"
  >
    {#each config?.rows ?? [] as row, rowIndex (rowIndex)}
      <div class="flex items-center" style="gap: {2 * scale}px;">
        {#each row.elements ?? [] as element, elementIndex (elementIndex)}
          {#if !(element.type === "chart" && element.chartConfig?.asBackground)}
            {#if element.type === "chart"}
              <div
                class="overflow-hidden rounded {showCharts ? '' : 'border border-current/10'}"
                style="width: {(element.width || 400) * scale}px; height: {(element.height || 200) * scale}px;"
              >
                {#if showCharts}
                  <ClockChart
                    chartConfig={element.chartConfig}
                    hours={element.hours}
                    glucoseUnits={faceUnits(config?.settings)}
                  />
                {/if}
              </div>
            {:else if element.type === "arrow"}
              {@const size = elementSize(element) * scale}
              {@const customCss = buildCustomCssString(element)}
              <div
                class="flex items-center"
                style="color: {getElementColor(element, currentBG, glucose.thresholds)}; opacity: {element.style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
              >
                <TrendArrow {direction} {size} />
              </div>
            {:else if element.type === "tracker" || element.type === "trackers"}
              {@const customCss = buildCustomCssString(element)}
              <ClockTrackers
                trackers={visibleTrackers(element, faceData.data)}
                show={element.show}
                size={elementSize(element) * scale}
                class="{getFontClass(element.style?.font)} {getFontWeightClass(element.style?.fontWeight)}"
                style="color: {getElementColor(element, currentBG, glucose.thresholds)}; opacity: {element.style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
              />
            {:else if element.type !== "chart"}
              <span
                class="leading-none tabular-nums {getFontClass(element.style?.font)} {getFontWeightClass(element.style?.fontWeight)} {isStale && element.type === 'sg' ? 'line-through opacity-60' : ''}"
                style={buildStyleString(element, currentBG, scale, glucose.thresholds)}
              >
                {renderClockElementValue(element, config.settings, glucose, currentTime, faceData.data)}
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
