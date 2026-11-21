import { describe, it, expect } from "vitest";
import { computeNightscoutTrackLayout, computeTrackLayout } from "./track-layout";

const noSwimLanes = { pumpMode: false, override: false, profile: false, activity: false };

function iobTrack(minIOB?: number) {
  const layout = computeTrackLayout(300, 400, 1, 2, { iob: true }, noSwimLanes, minIOB);
  return { layout, iobCob: layout.iobCob! };
}

describe("computeTrackLayout IOB track", () => {
  it("puts zero at the bottom when no IOB is negative", () => {
    const { layout, iobCob } = iobTrack();

    expect(iobCob.zero).toBe(layout.pixelToGlucoseDomain(iobCob.bottom));
    expect(iobCob.axisScale.domain()).toEqual([0, 2]);
  });

  it("raises zero above the bottom and spans the axis from the lowest IOB when IOB goes negative", () => {
    const { layout, iobCob } = iobTrack(-2);

    expect(iobCob.zero).toBeGreaterThan(layout.pixelToGlucoseDomain(iobCob.bottom));
    expect(iobCob.zero).toBeCloseTo(layout.pixelToGlucoseDomain((iobCob.top + iobCob.bottom) / 2));
    expect(iobCob.axisScale.domain()).toEqual([-2, 2]);
  });
});

describe("computeNightscoutTrackLayout", () => {
  const layout = computeNightscoutTrackLayout(400, 400, 2);

  it("gives glucose the whole plot on a log scale from 30 to 510 mg/dL", () => {
    const { glucose } = layout;

    expect(glucose.top).toBe(0);
    expect(glucose.bottom).toBe(400);
    expect(glucose.axisScale(30)).toBeCloseTo(400);
    expect(glucose.axisScale(510)).toBeCloseTo(0);
    // Equal ratios take equal heights on a log scale.
    expect(glucose.axisScale(60) - glucose.axisScale(120)).toBeCloseTo(
      glucose.axisScale(120) - glucose.axisScale(240)
    );
  });

  it("keeps values under the domain on its bottom edge", () => {
    expect(layout.glucose.scale(10)).toBeCloseTo(layout.glucose.scale(30));
  });

  it("hangs basal from the top over a quarter of the plot, up to 1.1 times the highest rate", () => {
    const basal = layout.basal!;

    expect(basal.top).toBe(0);
    expect(basal.bottom).toBe(100);
    expect(basal.zero).toBe(layout.pixelToGlucoseDomain(0));
    expect(basal.scale(2.2)).toBeCloseTo(layout.pixelToGlucoseDomain(100));
  });

  it("has no IOB/COB track or swim lanes", () => {
    expect(layout.iobCob).toBeNull();
    expect(Object.values(layout.swimLanes).some((lane) => lane.visible)).toBe(false);
  });
});
