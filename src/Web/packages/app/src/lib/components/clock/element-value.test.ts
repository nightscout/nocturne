import { describe, it, expect } from "vitest";
import type { ClockElement } from "$lib/api";
import {
  renderClockElementValue,
  type ClockElementValueContext,
} from "./element-value";
import { UNWIRED_ELEMENT_TYPES } from "$lib/clock-builder/types";

const ctx: ClockElementValueContext = {
  displayBG: "6.7",
  displayDelta: "+0.3",
  unitLabel: "mmol/L",
  age: "2m",
  time: "14:05",
};

const el = (element: ClockElement): ClockElement => element;

describe("renderClockElementValue", () => {
  it("renders the formatted glucose value", () => {
    expect(renderClockElementValue(el({ type: "sg" }), ctx)).toBe("6.7");
  });

  it("does not double the delta sign", () => {
    // displayDelta arrives from formatGlucoseDelta with its sign already applied.
    expect(renderClockElementValue(el({ type: "delta" }), ctx)).toBe(
      "+0.3 mmol/L"
    );
    expect(
      renderClockElementValue(el({ type: "delta", showUnits: false }), ctx)
    ).toBe("+0.3");
  });

  it("appends the unit label when showUnits is unset or true", () => {
    expect(
      renderClockElementValue(el({ type: "delta", showUnits: true }), ctx)
    ).toBe("+0.3 mmol/L");
    expect(
      renderClockElementValue(el({ type: "delta", showUnits: undefined }), ctx)
    ).toBe("+0.3 mmol/L");
  });

  it("renders reading age and time", () => {
    expect(renderClockElementValue(el({ type: "age" }), ctx)).toBe("2m ago");
    expect(renderClockElementValue(el({ type: "time" }), ctx)).toBe("14:05");
  });

  it("renders explicit placeholders for insulin and carbs on board", () => {
    expect(renderClockElementValue(el({ type: "iob" }), ctx)).toBe("--U");
    expect(renderClockElementValue(el({ type: "cob" }), ctx)).toBe("--g");
  });

  it("renders nothing for element types with no runtime data source", () => {
    // A saved face may still contain these; they must not print a plausible number.
    for (const type of UNWIRED_ELEMENT_TYPES) {
      expect(renderClockElementValue(el({ type }), ctx)).toBe("");
    }
  });

  it("renders custom text and nothing for icon-rendered types", () => {
    expect(renderClockElementValue(el({ type: "text", text: "Hi" }), ctx)).toBe(
      "Hi"
    );
    expect(renderClockElementValue(el({ type: "text" }), ctx)).toBe("");
    expect(renderClockElementValue(el({ type: "arrow" }), ctx)).toBe("");
    expect(renderClockElementValue(el({ type: "tracker" }), ctx)).toBe("");
  });
});

describe("ELEMENT_GROUPS", () => {
  it("does not offer element types with no runtime data source", async () => {
    const { ELEMENT_GROUPS } = await import("$lib/clock-builder/types");
    const offered = ELEMENT_GROUPS.flatMap((g) => g.types);
    for (const type of UNWIRED_ELEMENT_TYPES) {
      expect(offered).not.toContain(type);
    }
  });
});
