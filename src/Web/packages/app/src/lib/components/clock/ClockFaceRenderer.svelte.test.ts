import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import {
  NotificationUrgency,
  TrackerCategory,
  type ClockFaceConfig,
  type ClockFaceData,
} from "#lib/api/index.js";
import type { ClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
import { chartLineColorMode, glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { formatLocale } from "#lib/utils/formatting.js";
import { resolveChartThresholds, transformChartData } from "@nocturne/charts/core";
import type { ClockFaceDataSource } from "./clock-face-data.svelte";
import Harness from "./ClockFaceRendererHarness.test.svelte";

const WHITE = "rgb(255, 255, 255)";

const SITE = "0196c3a0-0000-7000-8000-000000000001";
const SENSOR = "0196c3a0-0000-7000-8000-000000000002";

const values: ClockFaceData = {
  iob: 1.2,
  trackers: [
    {
      definitionId: SITE,
      name: "Site",
      category: TrackerCategory.Cannula,
      urgency: NotificationUrgency.Info,
      hoursLeft: 52,
    },
    {
      definitionId: SENSOR,
      name: "Sensor",
      category: TrackerCategory.Sensor,
      urgency: NotificationUrgency.Warn,
      hoursLeft: -3,
    },
  ],
};

const withValues: ClockFaceDataSource = { data: values, chart: null, predictions: null };

/** A chart window with the last hour of readings, as the chart endpoint serves it. */
const withChart: ClockFaceDataSource = {
  data: null,
  chart: transformChartData({
    glucoseData: Array.from({ length: 12 }, (_, i) => ({
      time: Date.now() - (12 - i) * 5 * 60_000,
      sgv: 100 + i * 5,
    })),
  }),
  predictions: null,
};

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
        { type: "tracker", size: 14, definitionId: SITE, show: ["name", "remaining"] },
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
  faceConfig: ClockFaceConfig = config,
  faceData?: ClockFaceDataSource
) {
  const { container } = render(Harness, { config: faceConfig, glucose, faceData });
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
      faceData: withChart,
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
      faceData: withChart,
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

  it.each([
    ["a chart", { type: "chart", hours: 3, width: 400, height: 200 }],
    ["a background chart", { type: "chart", hours: 3, chartConfig: { asBackground: true } }],
  ] as const)("draws %s's glucose line in the viewer's line colour mode", async (_, element) => {
    chartLineColorMode.current = "threshold";
    try {
      const { container } = render(Harness, {
        config: { rows: [{ elements: [element, { type: "sg", size: 40 }] }] },
        glucose: reading,
        faceData: withChart,
        showCharts: true,
      });

      await vi.waitFor(() => {
        const stopColors = [...container.querySelectorAll("linearGradient stop")].map(
          (stop) => stop.getAttribute("stop-color")
        );
        expect(stopColors).toContain("var(--glucose-in-range)");
      });
    } finally {
      chartLineColorMode.current = "continuous";
    }
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

  it("shows the tracker the element names, with its time left", () => {
    const text = face(reading, config, withValues).text;

    expect(text).toContain("Site");
    expect(text).toContain("2d 4h left");
    expect(text).not.toContain("Sensor");
  });

  it("shows nothing for a tracker the API did not read, not a stand-in name", () => {
    expect(face(noReading).text.replace(/\s+/g, " ")).toBe("--");
  });

  it("shows only the trackers at or above a trackers element's threshold", () => {
    const trackersFace: ClockFaceConfig = {
      rows: [
        {
          elements: [
            { type: "trackers", size: 14, show: ["name", "remaining"], visibilityThreshold: "warn" },
          ],
        },
      ],
    };
    const text = face(reading, trackersFace, withValues).text;

    expect(text).toContain("Sensor");
    expect(text).toContain("3h late");
    expect(text).not.toContain("Site");
  });

  it("shows insulin on board from the face's values", () => {
    const iobFace: ClockFaceConfig = { rows: [{ elements: [{ type: "iob", size: 20 }] }] };

    expect(face(reading, iobFace, withValues).text).toBe("1.2U");
    expect(face(reading, iobFace).text).toBe("");
  });

  it("draws a chart from the face's chart window, without the realtime store", async () => {
    const { container } = render(Harness, {
      config: { rows: [{ elements: [{ type: "chart", hours: 3, width: 400, height: 200 }] }] },
      glucose: reading,
      faceData: withChart,
      showCharts: true,
    });

    await vi.waitFor(() => {
      expect(container.querySelector("[data-testid='clock-face-rows'] svg path")).not.toBeNull();
    });
  });
});
