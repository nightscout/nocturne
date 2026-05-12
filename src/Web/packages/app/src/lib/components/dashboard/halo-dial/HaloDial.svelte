<script lang="ts">
  // TODO(phase-5/6): rewrap children in a layerchart polar <Chart> so rings
  // can use <Spline>/<Arc> with per-vertex coloring; currently each child
  // falls back to plain <path>. See sibling components' "Fallback to <path>"
  // comments for the matching deferral.
  import { Tween } from "svelte/motion";
  import { cubicOut } from "svelte/easing";
  import { browser } from "$app/environment";
  import { getContext } from "svelte";
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { SettingsStore } from "$lib/stores/settings-store.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import { CENTER, VIEWBOX } from "./geometry";
  import { bgColor } from "./colors";
  import {
    defaultHaloDialConfig,
    HaloDialArcElement,
    HaloDialCenterSubElement,
    HaloDialPredictionCurve,
    type HaloDialConfig,
  } from "./config";
  import HistoryRing from "./ring/HistoryRing.svelte";
  import PredictionRing from "./ring/PredictionRing.svelte";
  import NowMarker from "./ring/NowMarker.svelte";
  import TrendChevron from "./ring/TrendChevron.svelte";
  import InnerArcSlot from "./inner/InnerArcSlot.svelte";
  import CornerSlot, { type CornerData } from "./corners/CornerSlot.svelte";
  import { getPredictions, type PredictionData } from "$api/predictions.remote";

  interface Props {
    /**
     * Override the resolved config; mainly for tests and the editor live
     * preview.
     */
    configOverride?: HaloDialConfig | null;
    /** Override the realtime store; mainly for tests. */
    realtimeOverride?: ReturnType<typeof tryGetRealtimeStore>;
  }

  const { configOverride = null, realtimeOverride = null }: Props = $props();

  // ---------- Store wiring ----------
  // The realtime store is mandatory in production but tests inject via override.
  const fallbackRealtime = tryGetRealtimeStore();
  const realtime = $derived(realtimeOverride ?? fallbackRealtime);

  // The settings store is optional here — if it isn't in context (tests, isolated
  // previews) we fall back to defaults. Settings store does not yet expose
  // `haloDial` as an individual section, so for now we always fall back to the
  // built-in defaults (Phase 6 will hook the real config in via the editor).
  const settingsStore = getContext<SettingsStore | undefined>(
    Symbol.for("settings-store")
  );
  void settingsStore; // intentional: read once Phase 6 surfaces haloDial.

  const config = $derived<HaloDialConfig>(
    configOverride ?? defaultHaloDialConfig()
  );

  // ---------- Live data ----------
  const currentBg = $derived(realtime?.currentBG ?? 0);
  const bgDelta = $derived(realtime?.bgDelta ?? 0);
  const lastUpdated = $derived(realtime?.lastUpdated ?? 0);
  const now = $derived(realtime?.now ?? Date.now());
  const pumpMode = $derived(realtime?.currentPumpMode ?? null);
  const sensitivityPercent = $derived(
    realtime?.currentSensitivityPercent ?? null
  );
  const pills = $derived(realtime?.pillsData ?? null);
  const direction = $derived(realtime?.direction ?? "Flat");

  // Cutoff rounded to one-minute buckets so the per-second `now` tick doesn't
  // invalidate `historyValues` (and the whole HistoryRing) 60 times a minute.
  // CGM entries arrive every 5 min, so a minute of cutoff drift is invisible.
  const cutoffMills = $derived(
    Math.floor((now - config.historyMinutes! * 60 * 1000) / 60_000) * 60_000
  );

  // History values (oldest → newest) within the configured window.
  const historyValues = $derived.by<number[]>(() => {
    const entries = realtime?.entries ?? [];
    if (entries.length === 0) return [];
    return entries
      .filter((e) => (e.mills ?? 0) >= cutoffMills)
      .map((e) => ({ mills: e.mills ?? 0, value: e.sgv ?? e.mgdl ?? 0 }))
      .filter((e) => e.value > 0)
      .sort((a, b) => a.mills - b.mills)
      .map((e) => e.value);
  });

  // ---------- Stale detection ----------
  const isStale = $derived(
    lastUpdated === 0 || now - lastUpdated > STALE_THRESHOLD_MS
  );

  // ---------- Predictions ----------
  let predictions = $state<PredictionData | null>(null);

  $effect(() => {
    // Re-fetch when BG meaningfully changes; skip if stale or empty.
    const bg = currentBg;
    const stale = isStale;
    if (bg <= 0 || stale) {
      predictions = null;
      return;
    }
    let active = true;
    getPredictions({})
      .run()
      .then((data) => {
        if (active) predictions = data;
      })
      .catch(() => {
        if (active) predictions = null;
      });
    return () => {
      active = false;
    };
  });

  function pickCurveValues(p: PredictionData | null): number[] | null {
    if (!p) return null;
    const c = p.curves;
    const byCurve = (() => {
      switch (config.predictionCurve) {
        case HaloDialPredictionCurve.Iob:
          return c.iobOnly;
        case HaloDialPredictionCurve.Uam:
          return c.uam;
        case HaloDialPredictionCurve.Cob:
          return c.cob;
        case HaloDialPredictionCurve.ZeroTemp:
          return c.zeroTemp;
        case HaloDialPredictionCurve.Main:
        default:
          return c.main;
      }
    })();
    // Fallback chain: selected → main → iobOnly → null.
    const fallback =
      byCurve && byCurve.length > 0
        ? byCurve
        : c.main && c.main.length > 0
          ? c.main
          : c.iobOnly && c.iobOnly.length > 0
            ? c.iobOnly
            : null;
    if (!fallback) return null;
    // Trim to predictionMinutes window.
    const horizonMs = config.predictionMinutes! * 60 * 1000;
    const baseMs = p.timestamp.getTime();
    return fallback
      .filter((pt) => pt.timestamp <= baseMs + horizonMs)
      .map((pt) => pt.value);
  }

  const predictionValues = $derived(pickCurveValues(predictions));

  // ---------- Center BG count-up ----------
  const reducedMotion =
    browser && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  const tweenedBg = Tween.of(() => Math.round(currentBg), {
    duration: reducedMotion ? 0 : 600,
    easing: cubicOut,
  });

  // ---------- Center sub text ----------
  function fmtDelta(d: number): string {
    const sign = d > 0 ? "+" : "";
    return `${sign}${d.toFixed(0)}`;
  }
  function fmtMinutes(ms: number): string {
    if (lastUpdated === 0) return "—";
    const mins = Math.floor((now - lastUpdated) / 60000);
    if (mins < 1) return "now";
    return `${mins}m`;
  }
  const centerSubText = $derived.by(() => {
    switch (config.centerSub) {
      case HaloDialCenterSubElement.MinutesAndDelta:
        return `${fmtMinutes(now - lastUpdated)} · ${fmtDelta(bgDelta)}`;
      case HaloDialCenterSubElement.MinutesOnly:
        return fmtMinutes(now - lastUpdated);
      case HaloDialCenterSubElement.DeltaOnly:
        return fmtDelta(bgDelta);
      case HaloDialCenterSubElement.Mmol:
        return (currentBg / 18).toFixed(1);
      case HaloDialCenterSubElement.None:
      default:
        return "";
    }
  });

  // ---------- Inner arc value picker ----------
  function arcValue(el: HaloDialArcElement | null | undefined): number | null {
    if (!el) return null;
    switch (el) {
      case HaloDialArcElement.Iob:
        return pills?.iob?.iob ?? null;
      case HaloDialArcElement.Cob:
        return pills?.cob?.cob ?? null;
      case HaloDialArcElement.BasalPercent:
        return pills?.basal?.tempBasal?.percent ?? null;
      case HaloDialArcElement.Sensitivity:
        return sensitivityPercent;
      default:
        return null;
    }
  }
  function arcMax(el: HaloDialArcElement | null | undefined): number {
    if (!el) return 1;
    switch (el) {
      case HaloDialArcElement.Iob:
        return config.iobMaxUnits ?? 8;
      case HaloDialArcElement.Cob:
        return config.cobMaxGrams ?? 80;
      case HaloDialArcElement.BasalPercent:
        return 200;
      case HaloDialArcElement.Sensitivity:
        return 200;
      default:
        return 1;
    }
  }

  // ---------- Corner data assembly ----------
  const cornerData = $derived<CornerData>({
    basalRate: pills?.basal
      ? {
          rate: pills.basal.totalBasal ?? 0,
          percent: pills.basal.tempBasal?.percent ?? 100,
        }
      : null,
    reservoir: null, // Not yet surfaced via pillsData; Phase 5/6 will hook reservoir DTO.
    sensorAge: null,
    pumpSiteAge: null,
    battery: null,
    loop: pills?.loop ? { status: pills.loop.status } : null,
    direction: { direction },
    eventual: predictions
      ? {
          mgdl: predictions.eventualBg,
          minutesAhead: config.predictionMinutes ?? 45,
        }
      : null,
  });

  const ringColor = $derived(bgColor(currentBg, config.colorMode!));
</script>

<div class="hd-card relative" data-testid="halo-dial" class:hd-stale={isStale}>
  <svg
    viewBox="0 0 {VIEWBOX} {VIEWBOX}"
    class="hd-svg"
    role="img"
    aria-label="Glucose dial"
    data-testid="halo-dial-svg"
  >
    <HistoryRing
      {historyValues}
      historyMinutes={config.historyMinutes ?? 15}
      predictionMinutes={config.predictionMinutes ?? 45}
      colorMode={config.colorMode!}
    />
    <PredictionRing
      {currentBg}
      {predictionValues}
      predictionMinutes={config.predictionMinutes ?? 45}
      colorMode={config.colorMode!}
      {pumpMode}
    />
    <NowMarker />
    {#if !isStale}
      <TrendChevron delta={bgDelta} color={ringColor} stale={false} />
    {/if}
    {#if config.innerLeftArc}
      <InnerArcSlot
        element={config.innerLeftArc}
        value={arcValue(config.innerLeftArc)}
        max={arcMax(config.innerLeftArc)}
        side="left"
      />
    {/if}
    {#if config.innerRightArc}
      <InnerArcSlot
        element={config.innerRightArc}
        value={arcValue(config.innerRightArc)}
        max={arcMax(config.innerRightArc)}
        side="right"
      />
    {/if}
    <text
      x={CENTER}
      y={CENTER}
      text-anchor="middle"
      dominant-baseline="middle"
      class="hd-center-bg"
      data-testid="halo-dial-center-bg"
    >
      {Math.round(tweenedBg.current)}
    </text>
    {#if centerSubText}
      <text
        x={CENTER}
        y={CENTER + 14}
        text-anchor="middle"
        dominant-baseline="middle"
        class="hd-center-sub"
        data-testid="halo-dial-center-sub"
      >
        {centerSubText}
      </text>
    {/if}
  </svg>

  <div class="hd-corner hd-corner-tl">
    <CornerSlot
      position="tl"
      elements={config.corners?.tl ?? []}
      data={cornerData}
      elementConfig={config.elementConfig ?? {}}
    />
  </div>
  <div class="hd-corner hd-corner-tr">
    <CornerSlot
      position="tr"
      elements={config.corners?.tr ?? []}
      data={cornerData}
      elementConfig={config.elementConfig ?? {}}
    />
  </div>
  <div class="hd-corner hd-corner-bl">
    <CornerSlot
      position="bl"
      elements={config.corners?.bl ?? []}
      data={cornerData}
      elementConfig={config.elementConfig ?? {}}
    />
  </div>
  <div class="hd-corner hd-corner-br">
    <CornerSlot
      position="br"
      elements={config.corners?.br ?? []}
      data={cornerData}
      elementConfig={config.elementConfig ?? {}}
    />
  </div>
</div>

<style>
  .hd-card {
    position: relative;
    aspect-ratio: 1 / 1;
    width: 100%;
    max-width: 280px;
    padding: 0.25rem;
  }
  .hd-svg {
    display: block;
    width: 100%;
    height: 100%;
  }
  .hd-stale {
    filter: grayscale(1);
    opacity: 0.55;
  }
  .hd-corner {
    position: absolute;
    pointer-events: none;
    font-size: 0.75rem;
    line-height: 1;
  }
  .hd-corner-tl {
    top: 0.25rem;
    left: 0.25rem;
  }
  .hd-corner-tr {
    top: 0.25rem;
    right: 0.25rem;
  }
  .hd-corner-bl {
    bottom: 0.25rem;
    left: 0.25rem;
  }
  .hd-corner-br {
    bottom: 0.25rem;
    right: 0.25rem;
  }
  .hd-center-bg {
    font-size: 28px;
    font-weight: 600;
    fill: var(--foreground);
  }
  .hd-center-sub {
    font-size: 9px;
    fill: var(--muted-foreground);
  }
</style>
