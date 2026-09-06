import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import type { ClockFaceConfig } from "$lib/api";
import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
import { TRACKER_SHOW_OPTIONS } from "$lib/clock-builder";
import Harness from "./ClockFaceRendererHarness.test.svelte";

const WHITE = "rgb(255, 255, 255)";

const config: ClockFaceConfig = {
  rows: [
    {
      elements: [
        { type: "sg", size: 40, style: { color: "dynamic" } },
        { type: "age", size: 14 },
        { type: "tracker", size: 14, show: ["name", "remaining", "urgency"] },
      ],
    },
  ],
  settings: { staleMinutes: 15 },
};

const reading: ClockGlucoseSource = {
  currentBG: 55,
  bgDelta: -3,
  direction: "Flat",
  lastUpdated: Date.now() - 7 * 60_000,
  demoMode: false,
};

const noReading: ClockGlucoseSource = {
  currentBG: null,
  bgDelta: null,
  direction: "",
  lastUpdated: null,
  demoMode: false,
};

/** Computed value of a CSS variable, to compare a rendered colour against. */
function cssColor(variable: string) {
  const probe = document.createElement("div");
  probe.style.backgroundColor = `var(${variable})`;
  document.body.append(probe);
  const color = getComputedStyle(probe).backgroundColor;
  probe.remove();
  return color;
}

function face(
  glucose: ClockGlucoseSource,
  faceConfig: ClockFaceConfig = config
) {
  const { container } = render(Harness, { config: faceConfig, glucose });
  const rows = container.querySelector("[data-testid='clock-face-rows']");
  const spans = [...(rows?.querySelectorAll("span") ?? [])];
  return {
    text: rows?.textContent?.trim() ?? "",
    background: getComputedStyle(container.firstElementChild!).backgroundColor,
    glucoseColor: (value: string) => {
      const span = spans.find((s) => s.textContent?.trim() === value);
      expect(span, `no element rendering "${value}"`).toBeTruthy();
      return getComputedStyle(span!).color;
    },
  };
}

describe("ClockFaceRenderer", () => {
  it("shows a placeholder in no glucose colour when there is no reading", () => {
    const { text, glucoseColor } = face(noReading);

    expect(text).toContain("--");
    expect(text).not.toMatch(/\d/);
    expect(glucoseColor("--")).toBe(WHITE);
  });

  it("shows the reading in its glucose colour", () => {
    const { text, glucoseColor } = face(reading);

    expect(text).toContain("55");
    expect(text).toContain("7m ago");
    expect(glucoseColor("55")).not.toBe(WHITE);
  });

  it("paints a muted element in the theme's muted foreground", () => {
    const muted: ClockFaceConfig = {
      rows: [{ elements: [{ type: "age", size: 14, style: { color: "muted" } }] }],
      settings: config.settings,
    };

    expect(face(reading, muted).glucoseColor("7m ago")).toBe(
      cssColor("--muted-foreground")
    );
  });

  it("does not paint the whole face with a colour nothing reported", () => {
    const glucoseColored = { ...config, settings: { bgColor: true } };
    const veryLow = cssColor("--glucose-very-low");

    expect(face(reading, glucoseColored).background).toBe(veryLow);
    expect(face(noReading, glucoseColored).background).not.toBe(veryLow);
  });

  it("renders nothing for tracker parts no data source backs", () => {
    const offered = TRACKER_SHOW_OPTIONS.map((option) => option.value);
    expect(offered).not.toContain("remaining");
    expect(offered).not.toContain("urgency");

    // The face above still asks for both; only the name may reach the screen.
    expect(face(reading).text).not.toContain("2d 4h");
    expect(face(noReading).text.replace(/\s+/g, " ")).toBe("-- Tracker");
  });
});
