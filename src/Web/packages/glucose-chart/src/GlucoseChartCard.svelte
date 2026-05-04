<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Chart, Svg, Axis } from 'layerchart';
  import { scaleTime, scaleLinear } from 'd3-scale';
  import { bisector } from 'd3';
  import type { TransformedChartData } from './types.js';
  import GlucoseTrack from './tracks/GlucoseTrack.svelte';
  import BasalTrack from './tracks/BasalTrack.svelte';
  import IobCobTrack from './tracks/IobCobTrack.svelte';
  import ChartLegend from './ChartLegend.svelte';
  import ChartTooltip from './ChartTooltip.svelte';
  import GlucoseInspectionDialog from './dialogs/GlucoseInspectionDialog.svelte';
  import TreatmentInspectionDialog from './dialogs/TreatmentInspectionDialog.svelte';
  import DeliveryInspectionDialog from './dialogs/DeliveryInspectionDialog.svelte';

  interface Props {
    data: TransformedChartData;
    width?: number;
    height?: number;
    carbRatio?: number;
    showLegend?: boolean;
    dialog: Snippet<[{ open: boolean; onOpenChange: (v: boolean) => void; children: Snippet }]>;
    badge: Snippet<[{ variant?: 'default' | 'outline' | 'destructive'; class?: string; children: Snippet }]>;
    button: Snippet<[{ onclick: () => void; variant?: string; size?: string; children: Snippet }]>;
  }

  let {
    data,
    width,
    height = 400,
    carbRatio = 15,
    showLegend = true,
    dialog,
    badge,
    button,
  }: Props = $props();

  // ===== TRACK HEIGHT PROPORTIONS =====
  // Basal: 12%, IOB/COB: 18%, Glucose: 70%
  const BASAL_RATIO = 0.12;
  const IOB_RATIO = 0.18;
  const GLUCOSE_RATIO = 0.70;

  const basalHeight = $derived(Math.round(height * BASAL_RATIO));
  const iobHeight = $derived(Math.round(height * IOB_RATIO));
  const glucoseHeight = $derived(height - basalHeight - iobHeight);

  // ===== X DOMAIN =====
  const xDomain = $derived.by((): [Date, Date] => {
    const g = data.glucoseData;
    if (g.length === 0) {
      const now = new Date();
      return [new Date(now.getTime() - 6 * 60 * 60 * 1000), now];
    }
    return [g[0].time, g[g.length - 1].time];
  });

  // ===== DERIVED DATA =====
  const glucoseData = $derived(data.glucoseData);
  const basalData = $derived(data.basalSeries);
  const iobData = $derived(data.iobSeries);
  const cobData = $derived(data.cobSeries);
  const bolusMarkers = $derived(data.bolusMarkers);
  const carbMarkers = $derived(data.carbMarkers);
  const deviceEventMarkers = $derived(data.deviceEventMarkers);
  const systemEventMarkers = $derived(data.systemEventMarkers);
  const pumpModeSpans = $derived(data.pumpModeSpans);
  const overrideSpans = $derived(data.overrideSpans);
  const profileSpans = $derived(data.profileSpans);
  const activitySpans = $derived(data.activitySpans);
  const tempBasalSpans = $derived(data.tempBasalSpans);
  const basalDeliverySpans = $derived(data.basalDeliverySpans);
  const heartRateSeries = $derived(data.heartRateSeries);
  const stepSeries = $derived(data.stepSeries);

  const maxBasalRate = $derived(data.maxBasalRate > 0 ? data.maxBasalRate : 2.5);
  const maxIob = $derived(data.maxIob > 0 ? data.maxIob : 3);
  const maxCob = $derived(data.maxCob > 0 ? data.maxCob : 60);
  const glucoseYMax = $derived(data.thresholds.glucoseYMax > 0 ? data.thresholds.glucoseYMax : 300);
  const highThreshold = $derived(data.thresholds.high);
  const lowThreshold = $derived(data.thresholds.low);
  const veryHighThreshold = $derived(data.thresholds.veryHigh);
  const veryLowThreshold = $derived(data.thresholds.veryLow);

  // Scheduled basal (for the dashed overlay line)
  const scheduledBasalData = $derived(
    basalData.map((d) => ({ timestamp: d.timestamp, rate: d.scheduledRate ?? d.rate }))
  );

  // Stale basal: none in the package version (no live sync), always null
  // eslint-disable-next-line prefer-const
  let staleBasalData: { start: Date; end: Date } | null = null;

  // ===== LEGEND TOGGLE STATE =====
  let showBasal = $state(true);
  let showIob = $state(true);
  let showCob = $state(true);
  let showBolus = $state(true);
  let showCarbs = $state(true);
  let showPumpModes = $state(true);
  let showAlarms = $state(true);
  let showScheduledTrackers = $state(true);
  let showOverrideSpans = $state(false);
  let showProfileSpans = $state(false);
  let showActivitySpans = $state(false);
  let expandedPumpModes = $state(false);

  const showIobTrack = $derived(showIob || showCob);

  // ===== DIALOG STATE =====
  let glucoseDialogOpen = $state(false);
  let treatmentDialogOpen = $state(false);
  let deliveryDialogOpen = $state(false);
  let inspectionTime = $state<Date | null>(null);

  // ===== FINDERS / BISECTORS =====
  const bisectDate = bisector((d: { time: Date }) => d.time).left;
  const bisectTimestamp = bisector((d: { timestamp?: number }) => d.timestamp ?? 0).left;

  function findSeriesValue<T extends { time: Date }>(series: T[], time: Date): T | undefined {
    const i = bisectDate(series, time, 1);
    const d0 = series[i - 1];
    const d1 = series[i];
    if (!d0) return d1;
    if (!d1) return d0;
    return time.getTime() - d0.time.getTime() > d1.time.getTime() - time.getTime() ? d1 : d0;
  }

  function findBasalPoint(time: Date) {
    if (!basalData || basalData.length === 0) return undefined;
    const timeMs = time.getTime();
    const i = bisectTimestamp(basalData, timeMs, 1);
    return basalData[i - 1];
  }

  const PROXIMITY_MS = 5 * 60 * 1000;

  function findNearbyBolus(time: Date) {
    return bolusMarkers.find((b) => Math.abs(b.time.getTime() - time.getTime()) < PROXIMITY_MS);
  }

  function findNearbyCarbs(time: Date) {
    return carbMarkers.find((c) => Math.abs(c.time.getTime() - time.getTime()) < PROXIMITY_MS);
  }

  function findNearbyDeviceEvent(time: Date) {
    return deviceEventMarkers.find((d) => Math.abs(d.time.getTime() - time.getTime()) < PROXIMITY_MS);
  }

  function findNearbySystemEvent(time: Date) {
    return systemEventMarkers.find((e) => Math.abs(e.time.getTime() - time.getTime()) < PROXIMITY_MS);
  }

  // Helper: add displayStart/displayEnd so returned spans satisfy DisplaySpan<T>
  function withDisplay<T extends { startTime: Date; endTime: Date | null }>(span: T): T & { displayStart: Date; displayEnd: Date } {
    return { ...span, displayStart: span.startTime, displayEnd: span.endTime ?? new Date() };
  }

  function findActiveSpanRaw<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    time: Date
  ): T | undefined {
    const ms = time.getTime();
    return spans.find((s) => {
      const start = s.startTime.getTime();
      const end = s.endTime?.getTime() ?? Date.now();
      return ms >= start && ms <= end;
    });
  }

  function findAllActiveSpansRaw<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    time: Date
  ): T[] {
    const ms = time.getTime();
    return spans.filter((s) => {
      const start = s.startTime.getTime();
      const end = s.endTime?.getTime() ?? Date.now();
      return ms >= start && ms <= end;
    });
  }

  const findActivePumpMode = (t: Date) => {
    const s = findActiveSpanRaw(pumpModeSpans, t);
    return s ? withDisplay(s) : undefined;
  };
  const findActiveOverride = (t: Date) => {
    const s = findActiveSpanRaw(overrideSpans, t);
    return s ? withDisplay(s) : undefined;
  };
  const findActiveProfile = (t: Date) => {
    const s = findActiveSpanRaw(profileSpans, t);
    if (!s) return undefined;
    return { ...withDisplay(s), profileName: (s.metadata?.profileName as string) ?? s.state ?? '' };
  };
  const findActiveActivities = (t: Date) => findAllActiveSpansRaw(activitySpans, t).map(withDisplay);
  const findActiveTempBasal = (t: Date) => {
    const s = findActiveSpanRaw(tempBasalSpans, t);
    if (!s) return undefined;
    return {
      ...withDisplay(s),
      rate: (s.metadata?.rate as number) ?? (s.metadata?.absolute as number) ?? null,
      percent: (s.metadata?.percent as number) ?? null,
    };
  };
  const findActiveBasalDelivery = (t: Date) => {
    const s = findActiveSpanRaw(basalDeliverySpans, t);
    return s ? withDisplay(s) : undefined;
  };
  const findIobValue = (t: Date) => findSeriesValue(iobData, t);
  const findCobValue = (t: Date) => findSeriesValue(cobData, t);

  // ===== PUMP MODE LEGEND HELPERS =====
  const currentPumpMode = $derived.by(() => {
    if (pumpModeSpans.length === 0) return undefined;
    const now = Date.now();
    const active = pumpModeSpans.find((s) => {
      const end = s.endTime?.getTime() ?? now + 1;
      return s.startTime.getTime() <= now && end >= now;
    });
    return active?.state;
  });

  const uniquePumpModes = $derived([...new Set(pumpModeSpans.map((s) => s.state))]);

  // ===== CLICK HANDLERS =====
  function handleGlucosePointClick(point: { time: Date; sgv: number; color: string }) {
    inspectionTime = point.time;
    glucoseDialogOpen = true;
  }

  function handleBasalPointClick(time: Date) {
    inspectionTime = time;
    deliveryDialogOpen = true;
  }

  function handleIobPointClick(time: Date) {
    inspectionTime = time;
    treatmentDialogOpen = true;
  }

  function handleMarkerClick(_treatmentId: string) {
    // In the package version, marker clicks open treatment dialog at nearest time
    treatmentDialogOpen = true;
  }

  // ===== DERIVED INSPECTION DATA =====
  // Glucose dialog needs full context at inspectionTime
  const inspectedGlucosePoint = $derived.by(() => {
    if (!inspectionTime) return null;
    return findSeriesValue(glucoseData, inspectionTime) ?? null;
  });

  const inspectedBasalPoint = $derived.by(() => {
    if (!inspectionTime) return null;
    return findBasalPoint(inspectionTime) ?? null;
  });

  const inspectedIob = $derived.by(() => {
    if (!inspectionTime) return undefined;
    return findIobValue(inspectionTime)?.value;
  });

  const inspectedCob = $derived.by(() => {
    if (!inspectionTime) return undefined;
    return findCobValue(inspectionTime)?.value;
  });

  const inspectedNearbyBolus = $derived.by(() => {
    if (!inspectionTime) return undefined;
    return findNearbyBolus(inspectionTime);
  });

  const inspectedNearbyCarbs = $derived.by(() => {
    if (!inspectionTime) return undefined;
    return findNearbyCarbs(inspectionTime);
  });

  const inspectedPrevGlucose = $derived.by(() => {
    if (!inspectionTime) return undefined;
    const idx = bisectDate(glucoseData, inspectionTime, 1);
    return idx >= 2 ? glucoseData[idx - 2]?.sgv : undefined;
  });

  const isStaleBasalAtInspection = $derived.by(() => {
    const stale = staleBasalData as { start: Date; end: Date } | null;
    if (!inspectionTime || !stale) return false;
    const t = inspectionTime.getTime();
    return t >= stale.start.getTime() && t <= stale.end.getTime();
  });

  // Tracker markers with display range for legend
  const scheduledTrackerMarkers = $derived(data.trackerMarkers);
</script>

<div style="width: {width ? width + 'px' : '100%'};">
  <!-- Legend -->
  {#if showLegend}
  <ChartLegend
    {glucoseData}
    {highThreshold}
    {lowThreshold}
    {veryHighThreshold}
    {veryLowThreshold}
    {showBasal}
    {showIob}
    {showCob}
    {showBolus}
    {showCarbs}
    {showPumpModes}
    {showAlarms}
    {showScheduledTrackers}
    {showOverrideSpans}
    {showProfileSpans}
    {showActivitySpans}
    onToggleBasal={() => (showBasal = !showBasal)}
    onToggleIob={() => (showIob = !showIob)}
    onToggleCob={() => (showCob = !showCob)}
    onToggleBolus={() => (showBolus = !showBolus)}
    onToggleCarbs={() => (showCarbs = !showCarbs)}
    onTogglePumpModes={() => (showPumpModes = !showPumpModes)}
    onToggleAlarms={() => (showAlarms = !showAlarms)}
    onToggleScheduledTrackers={() => (showScheduledTrackers = !showScheduledTrackers)}
    onToggleOverrideSpans={() => (showOverrideSpans = !showOverrideSpans)}
    onToggleProfileSpans={() => (showProfileSpans = !showProfileSpans)}
    onToggleActivitySpans={() => (showActivitySpans = !showActivitySpans)}
    deviceEventMarkers={deviceEventMarkers}
    systemEvents={systemEventMarkers}
    pumpModeSpans={pumpModeSpans}
    scheduledTrackerMarkers={scheduledTrackerMarkers}
    {currentPumpMode}
    {uniquePumpModes}
    {expandedPumpModes}
    onToggleExpandedPumpModes={() => (expandedPumpModes = !expandedPumpModes)}
  />
  {/if}

  <!-- Basal Chart -->
  {#if showBasal}
    <div style="height: {basalHeight}px;">
      <Chart
        data={basalData}
        x={(d) => new Date(d.timestamp ?? 0)}
        y={(d) => d.rate ?? 0}
        xScale={scaleTime()}
        xDomain={xDomain}
        yDomain={[0, maxBasalRate]}
        padding={{ left: 48, top: 4, bottom: 0, right: 48 }}
      >
        {#snippet children({ context })}
          {@const basalTrackHeight = context.height}
          {@const basalTrackTop = 0}
          {@const glucoseYMaxLocal = maxBasalRate}
          {@const pixelToGlucoseDomain = (py: number) => glucoseYMaxLocal * (1 - py / basalTrackHeight)}
          {@const basalScale = (rate: number) => pixelToGlucoseDomain(basalTrackTop + (rate / maxBasalRate) * basalTrackHeight)}
          {@const basalZero = pixelToGlucoseDomain(basalTrackTop)}
          {@const basalAxisScale = scaleLinear().domain([0, maxBasalRate]).range([basalTrackTop, basalTrackHeight])}
          <Svg>
            <BasalTrack
              {basalData}
              {scheduledBasalData}
              tempBasalSpans={tempBasalSpans.map((s) => ({
                ...s,
                displayStart: s.startTime,
                displayEnd: s.endTime ?? new Date(),
                rate: (s.metadata?.rate as number) ?? (s.metadata?.absolute as number) ?? null,
                percent: (s.metadata?.percent as number) ?? null,
              }))}
              {staleBasalData}
              {maxBasalRate}
              {basalScale}
              {basalZero}
              {basalTrackTop}
              {basalAxisScale}
              {context}
              {showBasal}
              onPointClick={handleBasalPointClick}
            />
          </Svg>
        {/snippet}
      </Chart>
    </div>
  {/if}

  <!-- Glucose Chart -->
  <div style="height: {glucoseHeight}px;">
    <Chart
      data={glucoseData}
      x="time"
      y="sgv"
      xScale={scaleTime()}
      xDomain={xDomain}
      yDomain={[0, glucoseYMax]}
      padding={{ left: 48, top: 8, bottom: 30, right: 48 }}
      tooltip={{ mode: 'quadtree-x' }}
    >
      {#snippet children({ context })}
        {@const trackHeight = context.height}
        {@const trackTop = 0}
        {@const trackBottom = trackHeight}
        {@const pixelToGlucoseDomain = (py: number) => glucoseYMax * (1 - py / trackHeight)}
        {@const glucoseScale = scaleLinear().domain([0, glucoseYMax]).range([pixelToGlucoseDomain(trackBottom), pixelToGlucoseDomain(trackTop)])}
        {@const glucoseAxisScale = scaleLinear().domain([0, glucoseYMax]).range([trackBottom, trackTop])}
        <Svg>
          <GlucoseTrack
            {glucoseData}
            {glucoseScale}
            {glucoseAxisScale}
            glucoseTrackTop={trackTop}
            {highThreshold}
            {lowThreshold}
            contextWidth={context.width}
            onPointClick={handleGlucosePointClick}
            {heartRateSeries}
            {stepSeries}
          />
          <Axis
            placement="bottom"
            format="hour"
            tickLabelProps={{ class: 'text-xs fill-muted-foreground' }}
          />
          <ChartTooltip
            {context}
            findBasalValue={findBasalPoint}
            findIobValue={findIobValue}
            findCobValue={findCobValue}
            {findNearbyBolus}
            {findNearbyCarbs}
            {findNearbyDeviceEvent}
            {findActivePumpMode}
            {findActiveOverride}
            {findActiveProfile}
            {findActiveActivities}
            {findActiveTempBasal}
            {findActiveBasalDelivery}
            {findNearbySystemEvent}
            {showBolus}
            {showCarbs}
            showDeviceEvents={true}
            {showIob}
            {showCob}
            {showBasal}
            {showPumpModes}
            {showOverrideSpans}
            {showProfileSpans}
            {showActivitySpans}
            {showAlarms}
            {staleBasalData}
          />
        </Svg>
      {/snippet}
    </Chart>
  </div>

  <!-- IOB/COB Chart -->
  {#if showIobTrack}
    <div style="height: {iobHeight}px;">
      <Chart
        data={iobData}
        x="time"
        y="value"
        xScale={scaleTime()}
        xDomain={xDomain}
        yDomain={[0, maxIob]}
        padding={{ left: 48, top: 4, bottom: 0, right: 48 }}
      >
        {#snippet children({ context })}
          {@const trackHeight = context.height}
          {@const trackTop = 0}
          {@const trackBottom = trackHeight}
          {@const pixelToGlucoseDomain = (py: number) => maxIob * (1 - py / trackHeight)}
          {@const iobScale = (v: number) => pixelToGlucoseDomain(trackBottom - (v / maxIob) * trackHeight)}
          {@const iobZero = pixelToGlucoseDomain(trackBottom)}
          {@const iobAxisScale = scaleLinear().domain([0, maxIob]).range([trackBottom, trackTop])}
          <Svg>
            <IobCobTrack
              {iobData}
              {cobData}
              {carbRatio}
              {iobScale}
              {iobZero}
              {iobAxisScale}
              iobTrackTop={trackTop}
              {showIob}
              {showCob}
              {showBolus}
              {showCarbs}
              bolusMarkers={bolusMarkers.map((m) => ({ ...m, treatmentId: m.treatmentId ?? undefined }))}
              carbMarkers={carbMarkers.map((m) => ({ ...m, treatmentId: m.treatmentId ?? undefined, label: m.label ?? undefined }))}
              {context}
              onMarkerClick={handleMarkerClick}
              {showIobTrack}
              onPointClick={handleIobPointClick}
            />
          </Svg>
        {/snippet}
      </Chart>
    </div>
  {/if}
</div>

<!-- Inspection Dialogs -->
<GlucoseInspectionDialog
  open={glucoseDialogOpen}
  timestamp={inspectionTime ?? new Date()}
  glucoseValue={inspectedGlucosePoint?.sgv ?? 0}
  glucoseColor={inspectedGlucosePoint?.color ?? 'var(--color-glucose-in-range)'}
  direction={inspectedGlucosePoint?.direction ?? undefined}
  previousGlucoseValue={inspectedPrevGlucose}
  dataSource={inspectedGlucosePoint?.dataSource ?? undefined}
  {glucoseData}
  {highThreshold}
  {lowThreshold}
  iob={inspectedIob}
  cob={inspectedCob}
  basalRate={inspectedBasalPoint?.rate}
  scheduledBasalRate={inspectedBasalPoint?.scheduledRate}
  basalOrigin={inspectedBasalPoint?.origin}
  hasDeliveryContext={inspectedBasalPoint != null}
  hasTreatmentContext={inspectedNearbyBolus != null || inspectedNearbyCarbs != null}
  onClose={() => { glucoseDialogOpen = false; inspectionTime = null; }}
  onNavigateDelivery={() => { glucoseDialogOpen = false; deliveryDialogOpen = true; }}
  onNavigateTreatment={() => { glucoseDialogOpen = false; treatmentDialogOpen = true; }}
  {dialog}
  {badge}
  {button}
/>

<TreatmentInspectionDialog
  open={treatmentDialogOpen}
  timestamp={inspectionTime ?? new Date()}
  bolusInsulin={inspectedNearbyBolus?.insulin}
  carbGrams={inspectedNearbyCarbs?.carbs}
  carbLabel={inspectedNearbyCarbs?.label ?? undefined}
  iob={inspectedIob}
  cob={inspectedCob}
  glucoseValue={inspectedGlucosePoint?.sgv}
  {glucoseData}
  {highThreshold}
  {lowThreshold}
  hasGlucoseContext={inspectedGlucosePoint != null}
  hasDeliveryContext={inspectedBasalPoint != null}
  onClose={() => { treatmentDialogOpen = false; inspectionTime = null; }}
  onNavigateGlucose={() => { treatmentDialogOpen = false; glucoseDialogOpen = true; }}
  onNavigateDelivery={() => { treatmentDialogOpen = false; deliveryDialogOpen = true; }}
  {dialog}
  {badge}
  {button}
/>

<DeliveryInspectionDialog
  open={deliveryDialogOpen}
  timestamp={inspectionTime ?? new Date()}
  basalRate={inspectedBasalPoint?.rate}
  scheduledBasalRate={inspectedBasalPoint?.scheduledRate}
  basalOrigin={inspectedBasalPoint?.origin}
  iob={inspectedIob}
  isStaleBasal={isStaleBasalAtInspection}
  {glucoseData}
  {highThreshold}
  {lowThreshold}
  hasGlucoseContext={inspectedGlucosePoint != null}
  hasTreatmentContext={inspectedNearbyBolus != null || inspectedNearbyCarbs != null}
  onClose={() => { deliveryDialogOpen = false; inspectionTime = null; }}
  onNavigateGlucose={() => { deliveryDialogOpen = false; glucoseDialogOpen = true; }}
  onNavigateTreatment={() => { deliveryDialogOpen = false; treatmentDialogOpen = true; }}
  {dialog}
  {badge}
  {button}
/>
