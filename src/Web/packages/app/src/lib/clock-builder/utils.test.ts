import { describe, it, expect } from "vitest";
import type { ClockElement } from "#lib/api/index.js";
import { FALLBACK_GLUCOSE_THRESHOLDS } from "#lib/constants/glucose-thresholds.js";
import {
  buildStyleString,
  clockBackgroundStyle,
  getElementColor,
} from "./utils";

const fallback = FALLBACK_GLUCOSE_THRESHOLDS;
const tenant = { veryLow: 60, low: 80, high: 160, veryHigh: 220 };

const dynamic = (): ClockElement => ({
  type: "sg",
  style: { color: "dynamic" },
});

describe("getElementColor", () => {
  it("takes no glucose colour when there is no reading", () => {
    expect(getElementColor(dynamic(), null, fallback)).toBe("#ffffff");
  });

  it("keeps a fixed colour whatever the reading", () => {
    const fixed: ClockElement = { type: "sg", style: { color: "#ff0000" } };
    expect(getElementColor(fixed, null, fallback)).toBe("#ff0000");
    expect(getElementColor(fixed, 55, fallback)).toBe("#ff0000");
  });

  it("resolves the muted token to the theme's muted foreground", () => {
    const muted: ClockElement = { type: "delta", style: { color: "muted" } };
    expect(getElementColor(muted, null, fallback)).toBe("var(--muted-foreground)");
    expect(getElementColor(muted, 55, fallback)).toBe("var(--muted-foreground)");
  });

  it("paints a dynamic element by the tenant's cut-points", () => {
    expect(getElementColor(dynamic(), 75, fallback)).toBe("var(--glucose-in-range)");
    expect(getElementColor(dynamic(), 75, tenant)).toBe("var(--glucose-low)");
    expect(getElementColor(dynamic(), 170, fallback)).toBe("var(--glucose-in-range)");
    expect(getElementColor(dynamic(), 170, tenant)).toBe("var(--glucose-high)");
  });

  it("paints a dynamic element in the reading's band colour", () => {
    expect(getElementColor(dynamic(), 40, fallback)).toBe("var(--glucose-very-low)");
    expect(getElementColor(dynamic(), 120, fallback)).toBe("var(--glucose-in-range)");
  });

  it("takes the default rather than emitting an unknown token as CSS", () => {
    const unknown: ClockElement = { type: "sg", style: { color: "subtle" } };
    expect(getElementColor(unknown, null, fallback)).toBe("#ffffff");
    expect(getElementColor({ type: "sg", style: {} }, null, fallback)).toBe("#ffffff");
  });

  it("does not resolve a token off the token map's prototype", () => {
    for (const inherited of ["constructor", "toString", "__proto__"]) {
      const element: ClockElement = { type: "sg", style: { color: inherited } };
      expect(getElementColor(element, null, fallback)).toBe("#ffffff");
    }
  });

  it("takes the default for a CSS colour that is not a hex literal", () => {
    for (const css of ["red", "rgb(255 0 0)", "hsl(0 100% 50%)"]) {
      const element: ClockElement = { type: "sg", style: { color: css } };
      expect(getElementColor(element, null, fallback)).toBe("#ffffff");
    }
  });
});

describe("buildStyleString", () => {
  it("scales the font size the caller renders at", () => {
    const element: ClockElement = { type: "sg", size: 20 };
    expect(buildStyleString(element, null, 2, fallback)).toContain("font-size: 40px");
    expect(buildStyleString(element, null, 0.8, fallback)).toContain("font-size: 16px");
  });

  it("sizes an element that carries no size off its type's default", () => {
    expect(buildStyleString({ type: "sg" }, null, 1, fallback)).toContain(
      "font-size: 40px"
    );
    expect(buildStyleString({ type: "age" }, null, 1, fallback)).toContain(
      "font-size: 10px"
    );
  });

  it("sizes an element of an unknown type off the global default", () => {
    expect(buildStyleString({ type: "wormhole" }, null, 1, fallback)).toContain(
      "font-size: 20px"
    );
  });
});

describe("clockBackgroundStyle", () => {
  const settings = { bgColor: true };

  it("takes the fallback, not a glucose colour, when there is no reading", () => {
    expect(clockBackgroundStyle(settings, null, "#0a0a0a", fallback)).toBe(
      "background-color: #0a0a0a;"
    );
  });

  it("colours the face by the tenant's cut-points", () => {
    expect(clockBackgroundStyle(settings, 75, "#0a0a0a", tenant)).toBe(
      "background-color: var(--glucose-low);"
    );
  });

  it("colours the face by a real reading", () => {
    expect(clockBackgroundStyle(settings, 55, "#0a0a0a", fallback)).toBe(
      "background-color: var(--glucose-low);"
    );
  });

  it("takes the fallback when the face is not coloured by glucose", () => {
    expect(clockBackgroundStyle({}, 55, "#0a0a0a", fallback)).toBe(
      "background-color: #0a0a0a;"
    );
    expect(clockBackgroundStyle(undefined, 55, "#0a0a0a", fallback)).toBe(
      "background-color: #0a0a0a;"
    );
  });

  it("prefers a background image over either", () => {
    expect(
      clockBackgroundStyle(
        { bgColor: true, backgroundImage: "/face.png" },
        55,
        "#0a0a0a",
        fallback
      )
    ).toContain("background-image: url(/face.png)");
  });
});
