import { describe, it, expect, vi } from "vitest";
import {
  BasalDeliveryOrigin,
  type ClockElement,
  type ClockFaceData,
  type ClockSettings,
} from "#lib/api/index.js";
import type {
  GlucoseUnits,
  TimeFormat,
} from "#lib/stores/appearance-store.svelte.js";
import { resolveChartThresholds } from "@nocturne/charts/core";
import type { ClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";

// formatting.ts pulls in appearance-store, which pulls in mode-watcher (no node
// export). Stub the chain, same as formatting.test.ts.
vi.mock("$app/env", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
  PersistedState: class {
    current: unknown;
    constructor(v: unknown) {
      this.current = v;
    }
  },
}));
vi.mock("#lib/stores/appearance-store.svelte.js", () => ({
  glucoseUnits: { current: "mg/dl" },
  timeFormat: { current: "12" },
  regionFormat: { current: "en-GB" },
  preferredLanguage: { current: "en" },
}));

const { renderClockElementValue } = await import("./element-value");
const store = await import("#lib/stores/appearance-store.svelte.js");

const now = new Date(2026, 11, 31, 14, 5);

const glucose = {
  currentBG: 120,
  bgDelta: 5,
  direction: "Flat",
  lastUpdated: now.getTime() - 7 * 60_000,
  demoMode: false,
  thresholds: resolveChartThresholds(),
};

/**
 * A tenant with no readings at all: a new tenant, or a connector not yet
 * syncing.
 */
const noReading = {
  currentBG: null,
  bgDelta: null,
  direction: "",
  lastUpdated: null,
  demoMode: false,
  thresholds: resolveChartThresholds(),
};

const el = (element: ClockElement): ClockElement => element;

const mgdl12: ClockSettings = { glucoseUnits: "mg/dl", timeFormat: "12" };
const mmol24: ClockSettings = { glucoseUnits: "mmol", timeFormat: "24" };

/** The viewer's own preferences, as a signed-in viewer's cookie would set them. */
function asViewer(
  units: GlucoseUnits,
  timeFormat: TimeFormat,
  run: () => void
) {
  const previous = [store.glucoseUnits.current, store.timeFormat.current];
  store.glucoseUnits.current = units;
  store.timeFormat.current = timeFormat;
  try {
    run();
  } finally {
    [store.glucoseUnits.current, store.timeFormat.current] = previous as [
      GlucoseUnits,
      TimeFormat,
    ];
  }
}

const render = (
  element: ClockElement,
  settings: ClockSettings | undefined = mgdl12,
  source: ClockGlucoseSource = glucose,
  data: ClockFaceData | null = null
) => renderClockElementValue(element, settings, source, now, data);

const faceData: ClockFaceData = {
  iob: 2.35,
  cob: 18,
  basal: { rate: 0.85, scheduledRate: 0.8, origin: BasalDeliveryOrigin.Algorithm },
  forecasts: [{ minutesAhead: 30, mgdl: 150 }],
  summaries: [
    { hours: 3, inRangePercent: 82.6, belowPercent: 2, abovePercent: 15.4, readings: 36 },
    { hours: 24, inRangePercent: 0, belowPercent: 0, abovePercent: 0, readings: 0 },
  ],
  trackers: [],
};

describe("renderClockElementValue", () => {
  it("shows a mmol, 24 hour face in mmol/L and 24 hour time to a viewer with no preferences", () => {
    asViewer("mg/dl", "12", () => {
      expect(render(el({ type: "sg" }), mmol24)).toBe("6.7 mmol/L");
      expect(render(el({ type: "delta" }), mmol24)).toBe("+0.3 mmol/L");
      expect(render(el({ type: "time" }), mmol24)).toBe("14:05");
    });
  });

  it("shows the face's settings to a viewer whose own preferences differ", () => {
    asViewer("mmol", "24", () => {
      expect(render(el({ type: "sg" }), mgdl12)).toBe("120 mg/dL");
      expect(render(el({ type: "delta" }), mgdl12)).toBe("+5 mg/dL");
      expect(render(el({ type: "time" }), mgdl12)).toMatch(/^02:05\s?[Pp]/);
    });
    asViewer("mg/dl", "12", () => {
      expect(render(el({ type: "sg" }), mmol24)).toBe("6.7 mmol/L");
      expect(render(el({ type: "time" }), mmol24)).toBe("14:05");
    });
  });

  it("reads a face saved before it carried settings as mg/dL and 12 hour", () => {
    asViewer("mmol", "24", () => {
      expect(render(el({ type: "sg" }), {})).toBe("120 mg/dL");
      expect(render(el({ type: "time" }), {})).toMatch(/^02:05\s?[Pp]/);
      expect(render(el({ type: "sg" }), undefined)).toBe("120 mg/dL");
    });
  });

  it("omits the unit label on sg and delta only when showUnits is false", () => {
    for (const type of ["sg", "delta"]) {
      const value = type === "sg" ? "6.7" : "+0.3";
      expect(render(el({ type, showUnits: false }), mmol24)).toBe(value);
      expect(render(el({ type, showUnits: true }), mmol24)).toBe(
        `${value} mmol/L`
      );
      expect(render(el({ type, showUnits: undefined }), mmol24)).toBe(
        `${value} mmol/L`
      );
    }
  });

  it("renders the reading age from the reading, not a sample", () => {
    expect(render(el({ type: "age" }))).toBe(
      new Intl.RelativeTimeFormat("en-GB", { style: "narrow", numeric: "auto" }).format(-7, "minute")
    );
  });

  it("reads a reading under a minute old as now", () => {
    expect(
      render(el({ type: "age" }), mgdl12, {
        ...glucose,
        lastUpdated: now.getTime(),
      })
    ).toBe(new Intl.RelativeTimeFormat("en-GB", { style: "narrow", numeric: "auto" }).format(0, "second"));
  });

  it("renders a placeholder, not a number, when there is no reading", () => {
    expect(render(el({ type: "sg" }), mgdl12, noReading)).toBe("--");
    expect(render(el({ type: "sg" }), mmol24, noReading)).toBe("--");
  });

  it("renders no delta and no age when there is no reading", () => {
    expect(render(el({ type: "delta" }), mgdl12, noReading)).toBe("");
    expect(render(el({ type: "age" }), mgdl12, noReading)).toBe("");
  });

  it("renders no delta from a lone reading", () => {
    expect(
      render(el({ type: "delta" }), mgdl12, { ...glucose, bgDelta: null })
    ).toBe("");
  });

  it("still renders the wall clock when there is no reading", () => {
    expect(render(el({ type: "time" }), mmol24, noReading)).toBe("14:05");
  });

  it("renders the values the API read for the face", () => {
    expect(render(el({ type: "iob" }), mgdl12, glucose, faceData)).toBe("2.35U");
    expect(render(el({ type: "cob" }), mgdl12, glucose, faceData)).toBe("18g");
    expect(render(el({ type: "basal" }), mgdl12, glucose, faceData)).toBe("0.85 U/h");
    expect(render(el({ type: "summary", hours: 3 }), mgdl12, glucose, faceData)).toBe(
      "83% in range"
    );
  });

  it("renders a forecast in the face's units, at the minutes ahead the element names", () => {
    expect(
      render(el({ type: "forecast", minutesAhead: 30 }), mgdl12, glucose, faceData)
    ).toBe("150 mg/dL");
    expect(
      render(el({ type: "forecast", minutesAhead: 30, showUnits: false }), mmol24, glucose, faceData)
    ).toBe("8.3");
    expect(render(el({ type: "forecast", minutesAhead: 60 }), mgdl12, glucose, faceData)).toBe("");
  });

  it("renders nothing, not a stand-in number, for a value the API did not read", () => {
    for (const type of ["iob", "cob", "basal", "forecast", "summary"]) {
      expect(render(el({ type }))).toBe("");
      expect(render(el({ type }), mgdl12, glucose, { trackers: [] } as ClockFaceData)).toBe("");
    }
  });

  it("renders no time in range for a window with no readings", () => {
    expect(render(el({ type: "summary", hours: 24 }), mgdl12, glucose, faceData)).toBe("");
  });

  it("renders custom text and nothing for icon-rendered types", () => {
    expect(render(el({ type: "text", text: "Hi" }))).toBe("Hi");
    expect(render(el({ type: "text" }))).toBe("");
    expect(render(el({ type: "arrow" }))).toBe("");
    expect(render(el({ type: "tracker" }))).toBe("");
  });
});

describe("ELEMENT_GROUPS", () => {
  it("offers every element type a face can show", async () => {
    const { ELEMENT_GROUPS } = await import("#lib/clock-builder/types.js");
    const offered = ELEMENT_GROUPS.flatMap((g) => g.types);
    for (const type of ["iob", "cob", "basal", "forecast", "summary", "tracker", "trackers", "chart"]) {
      expect(offered).toContain(type);
    }
  });
});
