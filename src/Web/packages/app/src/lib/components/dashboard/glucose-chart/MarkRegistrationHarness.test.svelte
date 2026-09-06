<script lang="ts">
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import {
    BasalDeliveryOrigin,
    ChartColor,
    DeviceEventType,
    SystemEventType,
  } from "$lib/api";
  import type { BasalPoint } from "$lib/api";
  import MarkCounter from "./MarkCounter.test.svelte";
  import BolusMarker from "./markers/BolusMarker.svelte";
  import CarbMarker from "./markers/CarbMarker.svelte";
  import DeviceEventMarker from "./markers/DeviceEventMarker.svelte";
  import SystemEventMarker from "./markers/SystemEventMarker.svelte";
  import TrackerExpirationMarker from "./markers/TrackerExpirationMarker.svelte";
  import BasalInjectionMarker from "./markers/BasalInjectionMarker.svelte";
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import { setGlucoseChartContext } from "./chart-context.svelte";
  import { computeTrackLayout } from "./engine/track-layout";
  import type { GlucoseChartContext } from "./chart-context.svelte";
  import type {
    ChartDataEngine,
    DisplayTempBasalSpan,
  } from "./engine/chart-data-engine.svelte";

  interface Props {
    counter: { marks: number; components: number };
    /** Datum count per marker kind, and per basal span / inferred step. */
    n?: number;
    width?: number;
    height?: number;
    onMarkerClick?: (treatmentId: string) => void;
  }

  let {
    counter,
    n = 60,
    width = 800,
    height = 400,
    onMarkerClick = () => {},
  }: Props = $props();

  const baseTime = new Date("2026-01-01T00:00:00Z").getTime();
  const stepMs = 5 * 60 * 1000;
  const at = (i: number) => new Date(baseTime + i * stepMs);

  const glucoseYMax = 400;
  const maxBasalRate = 3;

  const indices = $derived(Array.from({ length: n }, (_, i) => i));

  // Every basal point carries Inferred origin so the whole series lands in the
  // hatched branch — the densest per-datum path in BasalTrack.
  const basalData = $derived(
    indices.map(
      (i): BasalPoint => ({
        timestamp: at(i).getTime(),
        rate: 0.5 + (i % 4) * 0.25,
        origin: BasalDeliveryOrigin.Inferred,
        fillColor: ChartColor.InsulinTempBasal,
        strokeColor: ChartColor.InsulinTempBasal,
      }),
    ),
  );

  const tempBasalSpans = $derived(
    indices.map(
      (i): DisplayTempBasalSpan => ({
        id: `span-${i}`,
        startTime: at(i),
        endTime: at(i + 1),
        displayStart: at(i),
        displayEnd: at(i + 1),
        color: "var(--color-insulin-basal)",
        rate: i % 2 === 0 ? 0.75 : null,
        percent: i % 2 === 0 ? null : 120,
      }),
    ),
  );

  const staleBasalData = $derived({ start: at(0), end: at(n) });

  // Minimal ChartDataEngine stub — BasalTrack reads only these fields. The
  // `Partial<...> as ...` cast is deliberate: a newly-read engine field should
  // surface as a type error here rather than as runtime undefined.
  const engineStub = {
    get basalData() {
      return basalData;
    },
    get scheduledBasalData() {
      return [];
    },
    get displayTempBasalSpans() {
      return tempBasalSpans;
    },
    get staleBasalData() {
      return staleBasalData;
    },
    maxBasalRate,
  } as Partial<ChartDataEngine> as ChartDataEngine;

  const layout = $derived(
    computeTrackLayout(
      height,
      glucoseYMax,
      maxBasalRate,
      1,
      { basal: true, iob: false, cob: false },
      { pumpMode: false, override: false, profile: false, activity: false },
    ),
  );

  const ctx: GlucoseChartContext = {
    get engine() {
      return engineStub;
    },
    get layout() {
      return layout;
    },
  };
  setGlucoseChartContext(ctx);

  const xDomain = $derived<[Date, Date]>([at(0), at(n)]);
</script>

<div style="width: {width}px; height: {height}px;" data-testid="harness-root">
  <Chart
    data={basalData}
    x={(d: BasalPoint) => new Date(d.timestamp ?? 0)}
    y={(d: BasalPoint) => d.rate ?? 0}
    xScale={scaleTime()}
    {xDomain}
    yDomain={[0, glucoseYMax]}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
  >
    <Svg>
      <MarkCounter {counter} />
      <BasalTrack />
      {#each indices as i (i)}
        <BolusMarker
          xPos={i * 10}
          yPos={100}
          insulin={1.5}
          isOverride={i % 3 === 0}
          treatmentId="bolus-{i}"
          {onMarkerClick}
        />
        <CarbMarker
          xPos={i * 10}
          yPos={150}
          carbs={30}
          label={i % 2 === 0 ? "Lunch" : null}
          treatmentId="carb-{i}"
          {onMarkerClick}
        />
        <DeviceEventMarker
          xPos={i * 10}
          yPos={200}
          eventType={DeviceEventType.SiteChange}
          color="var(--color-muted-foreground)"
          treatmentId="event-{i}"
          {onMarkerClick}
        />
        <SystemEventMarker
          xPos={i * 10}
          yPos={250}
          eventType={SystemEventType.Alarm}
          color="var(--color-muted-foreground)"
        />
        <TrackerExpirationMarker
          xPos={i * 10}
          lineTop={0}
          lineBottom={height}
          basalTrackTop={0}
          time={at(i)}
          color="var(--color-muted-foreground)"
        />
        <BasalInjectionMarker
          xPos={i * 10}
          lineTop={0}
          lineBottom={height}
          units={12}
          insulinName="Lantus"
        />
      {/each}
    </Svg>
  </Chart>
</div>
