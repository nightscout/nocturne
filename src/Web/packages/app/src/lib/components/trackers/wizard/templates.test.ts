import { describe, expect, it } from "vitest";
import { eventList } from "./templates.svelte";

describe("tracker wizard templates", () => {
  it("names the device events of a tracker as a plain list", () => {
    expect(eventList(["Sensor Start", "Sensor Change"])).toBe("Sensor start, Sensor change");
    expect(eventList(["Sensor Start"])).toBe("Sensor start");
    expect(eventList([])).toBe("");
  });
});
