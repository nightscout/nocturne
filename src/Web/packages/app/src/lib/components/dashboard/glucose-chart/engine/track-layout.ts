import { scaleLinear } from "d3-scale";
import type { ScaleLinear } from "d3-scale";

export const SWIM_LANE_HEIGHT = 0.04;

export interface SwimLanePosition {
  top: number;
  bottom: number;
  visible: boolean;
}

export interface SwimLanePositions {
  pumpMode: SwimLanePosition;
  override: SwimLanePosition;
  profile: SwimLanePosition;
  activity: SwimLanePosition;
}

export interface TrackGeometry {
  top: number;
  bottom: number;
  height: number;
  /** Maps domain value → layerchart y-domain via pixelToGlucoseDomain */
  scale: (value: number) => number;
  /** Independent scale for axis rendering (pixel-space) */
  axisScale: ScaleLinear<number, number>;
  /** Zero line in layerchart y-domain coordinates */
  zero: number;
}

export interface TrackLayout {
  readonly basal: TrackGeometry | null;
  readonly glucose: TrackGeometry;
  readonly iobCob: TrackGeometry | null;
  readonly swimLanes: SwimLanePositions;
  readonly ratios: {
    basal: number;
    glucose: number;
    iob: number;
    swimLanes: number;
  };
  /** Reverse mapping from pixel y to glucose domain value */
  readonly pixelToGlucoseDomain: (pixelY: number) => number;
}

export interface TrackVisibility {
  basal?: boolean;
  iob?: boolean;
  cob?: boolean;
}

export interface SwimLaneData {
  pumpMode: boolean;
  override: boolean;
  profile: boolean;
  activity: boolean;
}

export function computeTrackLayout(
  chartHeight: number,
  glucoseYMax: number,
  maxBasalRate: number,
  maxIOB: number,
  visibility: TrackVisibility,
  swimLaneData: SwimLaneData
): TrackLayout {
  const showBasalTrack = visibility.basal ?? false;
  const showIobTrack = (visibility.iob ?? false) || (visibility.cob ?? false);

  const visibleSwimLaneCount = Object.values(swimLaneData).filter(Boolean).length;
  const swimLanesRatio = visibleSwimLaneCount * SWIM_LANE_HEIGHT;
  const basalRatio = showBasalTrack ? 0.12 : 0;
  const iobRatio = showIobTrack ? 0.18 : 0;
  const glucoseRatio = 1 - basalRatio - iobRatio - swimLanesRatio;

  const basalTrackHeight = chartHeight * basalRatio;
  const glucoseTrackHeight = chartHeight * glucoseRatio;
  const iobTrackHeight = chartHeight * iobRatio;

  const basalTrackTop = 0;
  const basalTrackBottom = basalTrackHeight;

  const swimLanes = computeSwimLanePositions(chartHeight, basalTrackBottom, swimLaneData);
  const swimLanesBottom = basalTrackBottom + swimLanesRatio * chartHeight;

  const glucoseTrackTop = swimLanesBottom;
  const glucoseTrackBottom = glucoseTrackTop + glucoseTrackHeight;
  const iobTrackTop = glucoseTrackBottom;
  const iobTrackBottom = iobTrackTop + iobTrackHeight;

  const pixelToGlucoseDomain = (pixelY: number) =>
    glucoseYMax * (1 - pixelY / chartHeight);

  const basal: TrackGeometry | null = showBasalTrack
    ? {
        top: basalTrackTop,
        bottom: basalTrackBottom,
        height: basalTrackHeight,
        scale: (rate: number) => {
          const pixelY = basalTrackTop + (rate / maxBasalRate) * basalTrackHeight;
          return pixelToGlucoseDomain(pixelY);
        },
        axisScale: scaleLinear()
          .domain([0, maxBasalRate])
          .range([basalTrackTop, basalTrackBottom]),
        zero: pixelToGlucoseDomain(basalTrackTop),
      }
    : null;

  const glucose: TrackGeometry = {
    top: glucoseTrackTop,
    bottom: glucoseTrackBottom,
    height: glucoseTrackHeight,
    scale: scaleLinear()
      .domain([0, glucoseYMax])
      .range([
        pixelToGlucoseDomain(glucoseTrackBottom),
        pixelToGlucoseDomain(glucoseTrackTop),
      ]) as unknown as (value: number) => number,
    axisScale: scaleLinear()
      .domain([0, glucoseYMax])
      .range([glucoseTrackBottom, glucoseTrackTop]),
    zero: pixelToGlucoseDomain(glucoseTrackBottom),
  };

  const iobCob: TrackGeometry | null = showIobTrack
    ? {
        top: iobTrackTop,
        bottom: iobTrackBottom,
        height: iobTrackHeight,
        scale: (value: number) => {
          const pixelY = iobTrackBottom - (value / maxIOB) * iobTrackHeight;
          return pixelToGlucoseDomain(pixelY);
        },
        axisScale: scaleLinear()
          .domain([0, maxIOB])
          .range([iobTrackBottom, iobTrackTop]),
        zero: pixelToGlucoseDomain(iobTrackBottom),
      }
    : null;

  return {
    basal,
    glucose,
    iobCob,
    swimLanes,
    ratios: { basal: basalRatio, glucose: glucoseRatio, iob: iobRatio, swimLanes: swimLanesRatio },
    pixelToGlucoseDomain,
  };
}

function computeSwimLanePositions(
  contextHeight: number,
  basalTrackBottom: number,
  swimLaneData: SwimLaneData
): SwimLanePositions {
  const swimLaneHeight = contextHeight * SWIM_LANE_HEIGHT;
  let currentY = basalTrackBottom;

  const positions: SwimLanePositions = {
    pumpMode: { top: 0, bottom: 0, visible: false },
    override: { top: 0, bottom: 0, visible: false },
    profile: { top: 0, bottom: 0, visible: false },
    activity: { top: 0, bottom: 0, visible: false },
  };

  const laneOrder = ["pumpMode", "override", "profile", "activity"] as const;
  for (const lane of laneOrder) {
    const visible = swimLaneData[lane];
    positions[lane] = {
      top: currentY,
      bottom: visible ? currentY + swimLaneHeight : currentY,
      visible,
    };
    if (visible) currentY += swimLaneHeight;
  }

  return positions;
}
