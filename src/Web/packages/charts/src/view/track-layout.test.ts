import { describe, it, expect } from "vitest";
import { computeTrackLayout } from "./track-layout";

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
