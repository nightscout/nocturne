import { describe, expect, it } from "vitest";
import { RailItemId } from "$lib/api/generated/nocturne-api-client";
import { effectiveRail, parseRailPreference, railOrder } from "./rail";

const { Cob, Basal, Iob, Loop, Reservoir, Trackers } = RailItemId;
const DEFAULT = [Cob, Basal, Iob, Loop, Reservoir, Trackers];

describe("parseRailPreference", () => {
  it("is undefined for a value that is not an object", () => {
    for (const raw of [undefined, null, "Cob", 3, [Cob, Iob]]) {
      expect(parseRailPreference(raw)).toBeUndefined();
    }
  });

  it("keeps known ids once each, in their stored order", () => {
    expect(
      parseRailPreference({ order: [Iob, Cob, Iob, "Future"], hidden: [Loop, Loop, 7] })
    ).toEqual({ order: [Iob, Cob], hidden: [Loop] });
  });

  it("drops a list that is not an array", () => {
    expect(parseRailPreference({ order: "Cob", hidden: { Loop: true } })).toEqual({});
  });

  it("leaves the default rail when every stored id is unknown", () => {
    const preference = parseRailPreference({ order: ["Future", "Gone"], hidden: ["Other"] });

    expect(preference).toEqual({ order: [], hidden: [] });
    expect(effectiveRail(preference, true)).toEqual(DEFAULT);
  });
});

describe("effectiveRail", () => {
  it("is the default rail for an empty preference", () => {
    expect(effectiveRail({}, true)).toEqual(DEFAULT);
    expect(effectiveRail(null, true)).toEqual(DEFAULT);
  });

  it("puts the ordered items first and the rest after in default order", () => {
    expect(railOrder({ order: [Reservoir, Iob] })).toEqual([
      Reservoir, Iob, Cob, Basal, Loop, Trackers,
    ]);
  });

  it("leaves out hidden items", () => {
    expect(effectiveRail({ order: [Loop], hidden: [Cob, Trackers] }, true)).toEqual([
      Loop, Basal, Iob, Reservoir,
    ]);
  });

  it("leaves out trackers while the tenant switch is off, whatever the user chose", () => {
    expect(effectiveRail({ order: [Trackers, Cob] }, false)).toEqual([
      Cob, Basal, Iob, Loop, Reservoir,
    ]);
  });
});
