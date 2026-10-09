import { describe, it, expect, vi } from "vitest";
import type { ClockElement, ClockSettings } from "$lib/api";
import type {
  GlucoseUnits,
  TimeFormat,
} from "$lib/stores/appearance-store.svelte";
import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";

// formatting.ts pulls in appearance-store, which pulls in mode-watcher (no node
// export). Stub the chain, same as formatting.test.ts.
vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
  PersistedState: class {
    current: unknown;
    constructor(v: unknown) {
      this.current = v;
    }
  },
}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
  glucoseUnits: { current: "mg/dl" },
  timeFormat: { current: "12" },
  regionFormat: { current: "en-GB" },
  preferredLanguage: { current: "en" },
}));

const { renderClockElementValue } = await import("./element-value");
const { UNWIRED_ELEMENT_TYPES } = await import("$lib/clock-builder/types");
const store = await import("$lib/stores/appearance-store.svelte");

const now = new Date(2026, 11, 31, 14, 5);

const glucose = {
  currentBG: 120,
  bgDelta: 5,
  direction: "Flat",
  lastUpdated: now.getTime() - 7 * 60_000,
  demoMode: false,
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
  source: ClockGlucoseSource = glucose
) => renderClockElementValue(element, settings, source, now);

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
    expect(render(el({ type: "age" }))).toBe("7m ago");
  });

  it("drops the preposition for a reading under a minute old", () => {
    expect(
      render(el({ type: "age" }), mgdl12, {
        ...glucose,
        lastUpdated: now.getTime(),
      })
    ).toBe("now");
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

  it("renders explicit placeholders for insulin and carbs on board", () => {
    expect(render(el({ type: "iob" }))).toBe("--U");
    expect(render(el({ type: "cob" }))).toBe("--g");
  });

  it("renders nothing for element types with no runtime data source", () => {
    // A saved face may still contain these; they must not print a plausible number.
    for (const type of UNWIRED_ELEMENT_TYPES) {
      expect(render(el({ type }))).toBe("");
    }
  });

  it("renders custom text and nothing for icon-rendered types", () => {
    expect(render(el({ type: "text", text: "Hi" }))).toBe("Hi");
    expect(render(el({ type: "text" }))).toBe("");
    expect(render(el({ type: "arrow" }))).toBe("");
    expect(render(el({ type: "tracker" }))).toBe("");
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
