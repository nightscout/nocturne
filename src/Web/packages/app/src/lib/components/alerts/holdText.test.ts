import { describe, it, expect } from "vitest";
import { heldFactName, heldReason, holdFactLines } from "./holdText.svelte";

describe("holdText", () => {
  it("names a glucose rule held by the sensor's state", () => {
    expect(holdFactLines({ facts: [{ fact: "sensor_state", reason: "sensor_not_ok" }] })).toEqual([
      "Sensor state: not OK, so Nocturne does not use the glucose value",
    ]);
  });

  it("names a sensor state the engine has no name for", () => {
    expect(holdFactLines({ facts: [{ fact: "sensor_state", reason: "unrecognised" }] })).toEqual([
      "Sensor state: has a value that Nocturne does not know",
    ]);
  });

  it("names a loop rule held by an old or missing result of the loop", () => {
    expect(
      holdFactLines({
        facts: [
          { fact: "aps_determination", reason: "stale" },
          { fact: "predictions", reason: "absent" },
        ],
      }),
    ).toEqual(["Loop result: too old", "Glucose forecast: missing"]);
  });

  it("names a rule held because the profile has no target range", () => {
    expect(holdFactLines({ facts: [{ fact: "target_range", reason: "absent" }] })).toEqual([
      "Target range: missing",
    ]);
  });

  it("shows a fact or reason this build does not know as the API sent it", () => {
    expect(heldFactName("sensor_mood")).toBe("sensor_mood");
    expect(heldReason("sensor_sulking")).toBe("sensor_sulking");
  });
});
