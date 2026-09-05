import { describe, it, expect } from "vitest";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import { knownTopWidgets } from "./widget-registry";

describe("knownTopWidgets", () => {
  it("drops ids with no widget behind them", () => {
    expect(
      knownTopWidgets([WidgetId.BgDelta, WidgetId.GlucoseChart, WidgetId.Tdd])
    ).toEqual([WidgetId.BgDelta, WidgetId.Tdd]);
  });

  it("drops inherited object keys", () => {
    expect(
      knownTopWidgets(["toString", "constructor", "__proto__", "valueOf"])
    ).toEqual([]);
  });
});
