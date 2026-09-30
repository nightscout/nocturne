import { describe, it, expect } from "vitest";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import { DEFAULT_TOP_WIDGETS, knownTopWidgets } from "./widget-registry";
import { withCurrentDefault } from "./top-widget-ids";

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

describe("withCurrentDefault", () => {
  it("reads the previous default as today's, with Current glucose last", () => {
    expect(
      withCurrentDefault([WidgetId.BgDelta, WidgetId.TirChart, WidgetId.Tdd])
    ).toEqual(DEFAULT_TOP_WIDGETS);
    expect(DEFAULT_TOP_WIDGETS.at(-1)).toBe(WidgetId.BgDelta);
  });

  it("keeps any other selection as stored", () => {
    const picked = [WidgetId.BgDelta, WidgetId.Tdd, WidgetId.TirChart];
    expect(withCurrentDefault(picked)).toBe(picked);
    const shorter = [WidgetId.BgDelta, WidgetId.TirChart];
    expect(withCurrentDefault(shorter)).toBe(shorter);
  });
});
