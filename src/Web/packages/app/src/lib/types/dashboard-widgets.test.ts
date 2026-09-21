import { describe, it, expect } from "vitest";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import { isMainSectionEnabled } from "./dashboard-widgets";

describe("isMainSectionEnabled", () => {
  it("shows a section the stored list does not name", () => {
    expect(isMainSectionEnabled(undefined, WidgetId.Statistics)).toBe(true);
    expect(isMainSectionEnabled([], WidgetId.Statistics)).toBe(true);
  });

  it("hides a section a stored row disables", () => {
    expect(
      isMainSectionEnabled(
        [{ id: WidgetId.Statistics, enabled: false }],
        WidgetId.Statistics
      )
    ).toBe(false);
  });
});
