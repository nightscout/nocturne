<script lang="ts">
  /**
   * The stage of the therapy timeline: each schedule in real time with what
   * was in effect over it, the glucose under them, and the pump, life and
   * device lanes, all on one time axis. Change marks run through every lane.
   */
  import { scaleTime } from "d3-scale";
  import {
    DeviceEventType,
    ProfileAttribute,
    type BasalDeliverySpanDto,
    type GlucoseTracePoint,
    type ProfileChange,
    type ProfileLaneStep,
    type ProfileLanes,
  } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { DeviceEventIcon } from "#lib/components/icons/index.js";
  import {
    bg,
    bgLabel,
    formatDateTimeCompact,
    formatMediumDateTime,
    formatShortDate,
    hourLabel,
  } from "#lib/utils/formatting.js";
  import type { GlucoseThresholds } from "#lib/utils/chart-colors.js";
  import TimelineLane, { type LaneBandPoint, type LanePoint } from "./TimelineLane.svelte";
  import SpanLane from "./SpanLane.svelte";
  import { DAY_MS, attributeUnit, attributeValue, fractionOf, isSwitch } from "./timeline-format.js";

  interface LaneSpan {
    id: string;
    label: string;
    startMills: number;
    endMills: number;
    color: string;
  }

  interface LaneMarker {
    id: string;
    mills: number;
    eventType: DeviceEventType;
  }

  export type LaneGroup = "profile" | "glucose" | "pump" | "life" | "devices";

  interface Props {
    window: [number, number];
    lanes: ProfileLanes | null;
    glucose: GlucoseTracePoint[] | null;
    basalDelivery: BasalDeliverySpanDto[] | null;
    pumpModes: LaneSpan[] | null;
    overrides: LaneSpan[] | null;
    activities: LaneSpan[] | null;
    sleep: LaneSpan[] | null;
    deviceEvents: LaneMarker[] | null;
    changes: ProfileChange[];
    selectedChange: number | null;
    hidden: ReadonlySet<LaneGroup>;
    thresholds: GlucoseThresholds & { glucoseYMax: number };
    onSelectChange?: (mills: number) => void;
  }

  let {
    window: stageWindow,
    lanes,
    glucose,
    basalDelivery,
    pumpModes,
    overrides,
    activities,
    sleep,
    deviceEvents,
    changes,
    selectedChange,
    hidden,
    thresholds,
    onSelectChange,
  }: Props = $props();

  const from = $derived(stageWindow[0]);
  const to = $derived(stageWindow[1]);

  function stepPoints(steps: ProfileLaneStep[] | undefined): LanePoint[] {
    const list = steps ?? [];
    if (list.length === 0) return [];
    const points = list.map((s) => ({ t: s.startMills ?? 0, v: s.value ?? 0 }));
    const last = list[list.length - 1];
    return [...points, { t: last.endMills ?? to, v: last.value ?? 0 }];
  }

  function bandPoints(steps: ProfileLaneStep[] | undefined): LaneBandPoint[] {
    const list = steps ?? [];
    if (list.length === 0) return [];
    const points = list.map((s) => ({
      t: s.startMills ?? 0,
      low: s.value ?? 0,
      high: s.high ?? s.value ?? 0,
    }));
    const last = points[points.length - 1];
    return [...points, { ...last, t: list[list.length - 1].endMills ?? to }];
  }

  function domain(values: number[], floorAtZero: boolean): [number, number] {
    if (values.length === 0) return [0, 1];
    const max = Math.max(...values);
    const min = floorAtZero ? 0 : Math.min(...values);
    const pad = (max - min || max || 1) * 0.15;
    return [floorAtZero ? 0 : Math.max(0, min - pad), max + pad];
  }

  const delivered = $derived.by((): LanePoint[] => {
    const spans = (basalDelivery ?? [])
      .filter((s) => (s.endMills ?? to) > from && (s.startMills ?? 0) < to)
      .sort((a, b) => (a.startMills ?? 0) - (b.startMills ?? 0));
    if (spans.length === 0) return [];
    const points = spans.map((s) => ({ t: Math.max(s.startMills ?? 0, from), v: s.rate ?? 0 }));
    const last = spans[spans.length - 1];
    return [...points, { t: Math.min(last.endMills ?? to, to), v: last.rate ?? 0 }];
  });

  const basalScheduled = $derived(stepPoints(lanes?.basal));
  const basalDomain = $derived(domain([...basalScheduled, ...delivered].map((p) => p.v), true));

  const sensitivityScheduled = $derived(stepPoints(lanes?.sensitivity));
  const sensitivityLoop = $derived(
    (lanes?.loop ?? []).map((p) => ({ t: p.mills ?? 0, v: p.sensitivity ?? 0 }))
  );
  const sensitivityDomain = $derived(
    domain([...sensitivityScheduled, ...sensitivityLoop].map((p) => p.v), false)
  );

  const carbRatioScheduled = $derived(stepPoints(lanes?.carbRatio));
  const carbRatioDomain = $derived(domain(carbRatioScheduled.map((p) => p.v), false));

  const targetBand = $derived(bandPoints(lanes?.targets));
  const temporaryTargets = $derived(
    (lanes?.temporaryTargets ?? []).map((s, i) => ({
      key: `temporary-${s.startMills}-${i}`,
      points: bandPoints([s]),
    }))
  );
  const targetDomain = $derived(
    domain(
      [...targetBand, ...temporaryTargets.flatMap((t) => t.points)].flatMap((p) => [p.low, p.high]),
      false
    )
  );

  const trace = $derived(
    (glucose ?? []).map((p) => ({
      t: p.mills ?? 0,
      v: p.average ?? 0,
      low: p.min ?? 0,
      high: p.max ?? 0,
    }))
  );

  /** Wide enough for the readings and the high threshold, so the trace fills the lane. */
  const glucoseMax = $derived(
    Math.min(thresholds.glucoseYMax, Math.max(thresholds.high * 1.4, ...trace.map((p) => p.high * 1.05)))
  );

  const hasProfile = $derived(
    basalScheduled.length + sensitivityScheduled.length + carbRatioScheduled.length + targetBand.length > 0
  );

  const visibleChanges = $derived(
    changes.filter((c) => (c.mills ?? 0) > from && (c.mills ?? 0) < to)
  );

  const ticks = $derived(scaleTime().domain([new Date(from), new Date(to)]).ticks(7));

  function isMidnight(tick: Date): boolean {
    return tick.getHours() === 0 && tick.getMinutes() === 0;
  }

  function tickLabel(tick: Date): string {
    return isMidnight(tick) ? formatShortDate(tick) : hourLabel(tick);
  }

  const DEVICE_LABELS: Partial<Record<DeviceEventType, string>> = {
    [DeviceEventType.SensorStart]: "Sensor change",
    [DeviceEventType.SensorChange]: "Sensor change",
    [DeviceEventType.TransmitterSensorInsert]: "Sensor change",
    [DeviceEventType.SiteChange]: "Site change",
    [DeviceEventType.CannulaChange]: "Site change",
    [DeviceEventType.InsulinChange]: "Reservoir change",
    [DeviceEventType.ReservoirChange]: "Reservoir change",
    [DeviceEventType.PodChange]: "Pod change",
    [DeviceEventType.PumpBatteryChange]: "Pump battery change",
  };

  const deviceMarkers = $derived(
    (deviceEvents ?? []).map((e) => ({
      id: e.id,
      mills: e.mills,
      title: DEVICE_LABELS[e.eventType] ?? "",
      eventType: e.eventType,
    }))
  );
  const eventTypeOf = $derived(new Map(deviceMarkers.map((m) => [m.id, m.eventType])));

  let plot = $state<HTMLDivElement | null>(null);
  let hoverAt = $state<number | null>(null);

  function onPointerMove(event: PointerEvent) {
    if (!plot) return;
    const box = plot.getBoundingClientRect();
    const fraction = (event.clientX - box.left) / box.width;
    hoverAt = fraction >= 0 && fraction <= 1 ? from + fraction * (to - from) : null;
  }

  function stepAt(steps: ProfileLaneStep[] | undefined, at: number): ProfileLaneStep | undefined {
    return (steps ?? []).find((s) => (s.startMills ?? 0) <= at && at < (s.endMills ?? 0));
  }

  /** The readings of the lanes at the pointer, as the lanes draw them. */
  const readout = $derived.by(() => {
    if (hoverAt === null) return [];
    const at = hoverAt;
    const items: { label: string; value: string }[] = [];

    const basal = stepAt(lanes?.basal, at);
    const rate = (basalDelivery ?? []).find((s) => (s.startMills ?? 0) <= at && at < (s.endMills ?? to));
    if (basal || rate) {
      const scheduled = basal ? `${attributeValue(ProfileAttribute.Basal, basal.value)} U/h` : null;
      const given = rate ? `${(rate.rate ?? 0).toFixed(2)} U/h delivered` : null;
      items.push({ label: "Basal", value: [scheduled, given].filter(Boolean).join(", ") });
    }

    const sensitivity = stepAt(lanes?.sensitivity, at);
    if (sensitivity) {
      const loop = (lanes?.loop ?? []).reduce<{ mills?: number; sensitivity?: number } | null>(
        (nearest, p) =>
          Math.abs((p.mills ?? 0) - at) < Math.abs((nearest?.mills ?? Infinity) - at) ? p : nearest,
        null
      );
      const near = loop && Math.abs((loop.mills ?? 0) - at) <= 10 * 60_000 ? loop : null;
      items.push({
        label: "Sensitivity",
        value:
          `${attributeValue(ProfileAttribute.Sensitivity, sensitivity.value)} ${attributeUnit(ProfileAttribute.Sensitivity)}` +
          (near
            ? `, ${bg(near.sensitivity ?? 0)} ${attributeUnit(ProfileAttribute.Sensitivity)} from the loop`
            : ""),
      });
    }

    const carbRatio = stepAt(lanes?.carbRatio, at);
    if (carbRatio) {
      items.push({ label: "Carb ratio", value: attributeValue(ProfileAttribute.CarbRatio, carbRatio.value) });
    }

    const target = stepAt(lanes?.temporaryTargets, at) ?? stepAt(lanes?.targets, at);
    if (target) {
      items.push({
        label: stepAt(lanes?.temporaryTargets, at) ? "Temporary target" : "Target",
        value: `${attributeValue(ProfileAttribute.TargetRange, target.value, target.high)} ${bgLabel()}`,
      });
    }

    if (trace.length > 0) {
      const nearest = trace.reduce((a, b) => (Math.abs(b.t - at) < Math.abs(a.t - at) ? b : a));
      const bucket = trace.length > 1 ? trace[1].t - trace[0].t : 0;
      if (Math.abs(nearest.t - at) <= Math.max(bucket, 5 * 60_000)) {
        items.push({ label: "Glucose", value: `${bg(nearest.v)} ${bgLabel()}` });
      }
    }

    return items;
  });
</script>

<div
  class="relative [--tl-axis:2.75rem] [--tl-label:5rem] @2xl:[--tl-label:6.5rem]"
  role="group"
  aria-label="Therapy timeline lanes"
  onpointermove={onPointerMove}
  onpointerleave={() => (hoverAt = null)}
>
  <!-- A fixed-height readout, so lanes do not move when the pointer enters. -->
  <div class="flex min-h-8 flex-wrap items-baseline gap-x-4 gap-y-0.5 pb-2 text-xs" aria-live="off">
    {#if hoverAt !== null}
      <span class="font-medium tabular-nums">{formatMediumDateTime(new Date(hoverAt))}</span>
      {#each readout as item (item.label)}
        <span class="tabular-nums">
          <span class="text-muted-foreground">{item.label}</span>
          {item.value}
        </span>
      {/each}
    {:else}
      <span class="text-muted-foreground">Point at the lanes to read the values at a time.</span>
    {/if}
  </div>

  <div class="relative pt-5">
    {#if !hidden.has("profile")}
      {#if lanes === null}
        <!-- The versions card above says why the profile lanes are absent. -->
      {:else if !hasProfile}
        <p class="border-t border-border py-4 text-sm text-muted-foreground">
          No profile is recorded for this time.
        </p>
      {:else}
        <TimelineLane
          label="Basal"
          unit="U/h"
          xDomain={stageWindow}
          yDomain={basalDomain}
          areas={[{ key: "delivered", points: delivered, class: "fill-basal/40 stroke-basal stroke-1" }]}
          lines={[
            {
              key: "scheduled",
              points: basalScheduled,
              step: true,
              dash: "4,3",
              class: "stroke-foreground/70 stroke-2",
            },
          ]}
          axisFormat={(v) => v.toFixed(v < 10 ? 1 : 0)}
          heightClass="h-20"
        />
        <TimelineLane
          label="Sensitivity"
          unit={attributeUnit(ProfileAttribute.Sensitivity)}
          xDomain={stageWindow}
          yDomain={sensitivityDomain}
          lines={[
            { key: "loop", points: sensitivityLoop, class: "stroke-insulin stroke-1" },
            {
              key: "scheduled",
              points: sensitivityScheduled,
              step: true,
              dash: "4,3",
              class: "stroke-foreground/70 stroke-2",
            },
          ]}
          axisFormat={(v) => String(bg(v))}
        />
        <TimelineLane
          label="Carb ratio"
          unit="g/U"
          xDomain={stageWindow}
          yDomain={carbRatioDomain}
          lines={[
            {
              key: "scheduled",
              points: carbRatioScheduled,
              step: true,
              dash: "4,3",
              class: "stroke-foreground/70 stroke-2",
            },
          ]}
          axisFormat={(v) => v.toFixed(v < 10 ? 1 : 0)}
        />
        <TimelineLane
          label="Target"
          unit={bgLabel()}
          xDomain={stageWindow}
          yDomain={targetDomain}
          bands={[
            {
              key: "scheduled",
              points: targetBand,
              step: true,
              class: "fill-glucose-in-range/25 stroke-glucose-in-range stroke-1",
            },
            ...temporaryTargets.map((t) => ({
              key: t.key,
              points: t.points,
              step: true,
              class: "fill-foreground/30 stroke-foreground stroke-1",
            })),
          ]}
          axisFormat={(v) => String(bg(v))}
        />
      {/if}
    {/if}

    {#if !hidden.has("glucose") && glucose !== null}
      {#if trace.length > 1}
        <TimelineLane
          label="Glucose"
          unit={bgLabel()}
          xDomain={stageWindow}
          yDomain={[0, glucoseMax]}
          bands={[
            {
              key: "range",
              points: [
                { t: from, low: thresholds.low, high: thresholds.high },
                { t: to, low: thresholds.low, high: thresholds.high },
              ],
              class: "fill-glucose-in-range/10",
            },
            { key: "spread", points: trace, class: "fill-glucose-in-range/25" },
          ]}
          lines={[{ key: "average", points: trace, class: "stroke-glucose-in-range stroke-2" }]}
          axisFormat={(v) => String(bg(v))}
        />
      {:else}
        <p class="border-t border-border py-3 text-sm text-muted-foreground">
          No glucose readings in this time.
        </p>
      {/if}
    {/if}

    {#if !hidden.has("pump")}
      {#if pumpModes !== null}
        <SpanLane label="Pump mode" xDomain={stageWindow} spans={pumpModes} />
      {/if}
      {#if overrides !== null}
        <SpanLane label="Override" xDomain={stageWindow} spans={overrides} />
      {/if}
    {/if}

    {#if !hidden.has("life")}
      {#if activities !== null}
        <SpanLane label="Activity" xDomain={stageWindow} spans={activities} />
      {/if}
      {#if sleep !== null}
        <SpanLane label="Sleep" xDomain={stageWindow} spans={sleep} />
      {/if}
    {/if}

    {#if !hidden.has("devices") && deviceEvents !== null}
      <SpanLane label="Devices" xDomain={stageWindow} markers={deviceMarkers}>
        {#snippet marker(point)}
          <DeviceEventIcon eventType={eventTypeOf.get(point.id)} size={12} />
        {/snippet}
      </SpanLane>
    {/if}

    <!-- The plot column, over every lane: the change marks and the pointer's rule. -->
    <div
      bind:this={plot}
      class="pointer-events-none absolute inset-y-0 right-(--tl-axis) left-(--tl-label)"
    >
      {#each visibleChanges as change (change.mills)}
        {@const selected = selectedChange === change.mills}
        <div
          class="absolute inset-y-0 left-(--mark-left) w-px {selected ? 'bg-foreground' : 'bg-foreground/35'}"
          style:--mark-left="{fractionOf(change.mills ?? 0, from, to) * 100}%"
        >
          <Button
            variant="ghost"
            size="icon-2xs"
            class="pointer-events-auto absolute top-0 left-1/2 -translate-x-1/2"
            aria-label="{isSwitch(change) ? 'Profile switch' : 'Profile edit'}, {formatDateTimeCompact(
              new Date(change.mills ?? 0)
            )}"
            aria-pressed={selected}
            onclick={() => onSelectChange?.(change.mills ?? 0)}
          >
            <span
              class="block size-2 rotate-45 border {selected
                ? 'scale-150 border-foreground bg-foreground'
                : isSwitch(change)
                  ? 'border-foreground bg-foreground/70'
                  : 'border-foreground bg-card'}"
            ></span>
          </Button>
        </div>
      {/each}
      {#if hoverAt !== null}
        <div
          class="absolute top-5 bottom-0 left-(--hover-left) w-px bg-muted-foreground/60"
          style:--hover-left="{fractionOf(hoverAt, from, to) * 100}%"
        ></div>
      {/if}
    </div>
  </div>

  <div class="grid grid-cols-[var(--tl-label)_minmax(0,1fr)_var(--tl-axis)] border-t border-border">
    <div></div>
    <div class="relative h-5 text-2xs text-muted-foreground tabular-nums" aria-hidden="true">
      {#each ticks as tick (tick.getTime())}
        <span
          class="absolute top-1 left-(--tick-left) -translate-x-1/2 whitespace-nowrap {isMidnight(tick) || to - from <= DAY_MS
            ? ''
            : 'max-sm:hidden'}"
          style:--tick-left="{fractionOf(tick.getTime(), from, to) * 100}%"
        >
          {tickLabel(tick)}
        </span>
      {/each}
    </div>
    <div></div>
  </div>
</div>
