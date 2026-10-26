import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import type { ClockFaceConfig } from "#lib/api/index.js";
import type { ClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
import { TRACKER_SHOW_OPTIONS } from "#lib/clock-builder/index.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { formatLocale } from "#lib/utils/formatting.js";
import { resolveChartThresholds } from "#lib/constants/glucose-thresholds.js";
import Harness from "./ClockFaceRendererHarness.test.svelte";

const WHITE = "rgb(255, 255, 255)";

const sevenMinutesAgo = new Intl.RelativeTimeFormat(formatLocale(), {
  style: "narrow",
  numeric: "auto",
}).format(-7, "minute");

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
  thresholds: resolveChartThresholds(),
};

const noReading: ClockGlucoseSource = {
  currentBG: null,
  bgDelta: null,
  direction: "",
  lastUpdated: null,
  demoMode: false,
  thresholds: resolveChartThresholds(),
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

    expect(text).toContain("55 mg/dL");
    expect(text).toContain(sevenMinutesAgo);
    expect(glucoseColor("55 mg/dL")).not.toBe(WHITE);
  });

  it("colours the reading by the tenant's thresholds when the source carries them", () => {
    const at75 = { ...reading, currentBG: 75 };
    const fallbackColor = face(at75).glucoseColor("75 mg/dL");
    const tenantColor = face({
      ...at75,
      thresholds: { ...resolveChartThresholds(), low: 80, high: 160 },
    }).glucoseColor("75 mg/dL");

    expect(fallbackColor).toBe(cssColor("--glucose-in-range"));
    expect(tenantColor).toBe(cssColor("--glucose-low"));
  });

  it("renders in the face's units, not the viewer's", () => {
    const mmolFace: ClockFaceConfig = {
      ...config,
      settings: { ...config.settings, glucoseUnits: "mmol", timeFormat: "24" },
    };

    expect(face(reading, mmolFace).text).toContain("3.1 mmol/L");
  });

  it("draws a chart's axis ticks in the face's units, not the viewer's", async () => {
    glucoseUnits.current = "mg/dl";
    const chartFace: ClockFaceConfig = {
      rows: [{ elements: [{ type: "chart", hours: 3, width: 400, height: 200 }] }],
      settings: { glucoseUnits: "mmol" },
    };
    const { container } = render(Harness, {
      config: chartFace,
      glucose: reading,
      showCharts: true,
    });

    await vi.waitFor(() => {
      const ticks = [...container.querySelectorAll(".lc-axis-tick-label")].map(
        (t) => t.textContent?.trim() ?? ""
      );
      expect(ticks.length).toBeGreaterThan(0);
      expect(ticks.some((t) => /^\d+\.\d$/.test(t))).toBe(true);
    });
  });

  it("draws a background chart's axis ticks in the face's units, not the viewer's", async () => {
    glucoseUnits.current = "mg/dl";
    const backgroundFace: ClockFaceConfig = {
      rows: [
        {
          elements: [
            { type: "chart", hours: 3, chartConfig: { asBackground: true } },
            { type: "sg", size: 40 },
          ],
        },
      ],
      settings: { glucoseUnits: "mmol" },
    };
    const { container } = render(Harness, {
      config: backgroundFace,
      glucose: reading,
      showCharts: true,
    });

    await vi.waitFor(() => {
      const ticks = [...container.querySelectorAll(".lc-axis-tick-label")].map(
        (t) => t.textContent?.trim() ?? ""
      );
      expect(ticks.length).toBeGreaterThan(0);
      expect(ticks.some((t) => /^\d+\.\d$/.test(t))).toBe(true);
    });
  });

  it("paints a muted element in the theme's muted foreground", () => {
    const muted: ClockFaceConfig = {
      rows: [{ elements: [{ type: "age", size: 14, style: { color: "muted" } }] }],
      settings: config.settings,
    };

    expect(face(reading, muted).glucoseColor(sevenMinutesAgo)).toBe(
      cssColor("--muted-foreground")
    );
  });

  it("does not paint the whole face with a colour nothing reported", () => {
    const glucoseColored = { ...config, settings: { bgColor: true } };
    const low = cssColor("--glucose-low");

    expect(face(reading, glucoseColored).background).toBe(low);
    expect(face(noReading, glucoseColored).background).not.toBe(low);
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
