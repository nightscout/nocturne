import { describe, it, expect, vi } from "vitest";
import type { ClockElement } from "$lib/api";
import type { GlucoseUnits } from "$lib/stores/appearance-store.svelte";

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
  timeFormat: { current: "24" },
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

function withUnits(units: GlucoseUnits, run: () => void) {
  const previous = store.glucoseUnits.current;
  store.glucoseUnits.current = units;
  try {
    run();
  } finally {
    store.glucoseUnits.current = previous;
  }
}

describe("renderClockElementValue", () => {
  it("renders glucose in the viewer's units", () => {
    withUnits("mg/dl", () => {
      expect(renderClockElementValue(el({ type: "sg" }), glucose, now)).toBe(
        "120"
      );
    });
    withUnits("mmol", () => {
      expect(renderClockElementValue(el({ type: "sg" }), glucose, now)).toBe(
        "6.7"
      );
    });
  });

  it("renders the delta and its unit label in the viewer's units", () => {
    withUnits("mg/dl", () => {
      expect(renderClockElementValue(el({ type: "delta" }), glucose, now)).toBe(
        "+5 mg/dL"
      );
    });
    withUnits("mmol", () => {
      expect(renderClockElementValue(el({ type: "delta" }), glucose, now)).toBe(
        "+0.3 mmol/L"
      );
    });
  });

  it("omits the unit label only when showUnits is false", () => {
    withUnits("mmol", () => {
      expect(
        renderClockElementValue(
          el({ type: "delta", showUnits: false }),
          glucose,
          now
        )
      ).toBe("+0.3");
      expect(
        renderClockElementValue(
          el({ type: "delta", showUnits: true }),
          glucose,
          now
        )
      ).toBe("+0.3 mmol/L");
      expect(
        renderClockElementValue(
          el({ type: "delta", showUnits: undefined }),
          glucose,
          now
        )
      ).toBe("+0.3 mmol/L");
    });
  });

  it("renders the reading age from the reading, not a sample", () => {
    expect(renderClockElementValue(el({ type: "age" }), glucose, now)).toBe(
      "7m ago"
    );
  });

  it("drops the preposition for a reading under a minute old", () => {
    expect(
      renderClockElementValue(
        el({ type: "age" }),
        { ...glucose, lastUpdated: now.getTime() },
        now
      )
    ).toBe("now");
  });

  it("renders a placeholder, not a number, when there is no reading", () => {
    withUnits("mg/dl", () => {
      expect(renderClockElementValue(el({ type: "sg" }), noReading, now)).toBe(
        "--"
      );
    });
    withUnits("mmol", () => {
      expect(renderClockElementValue(el({ type: "sg" }), noReading, now)).toBe(
        "--"
      );
    });
  });

  it("renders no delta and no age when there is no reading", () => {
    expect(renderClockElementValue(el({ type: "delta" }), noReading, now)).toBe(
      ""
    );
    expect(renderClockElementValue(el({ type: "age" }), noReading, now)).toBe(
      ""
    );
  });

  it("renders no delta from a lone reading", () => {
    expect(
      renderClockElementValue(
        el({ type: "delta" }),
        { ...glucose, bgDelta: null },
        now
      )
    ).toBe("");
  });

  it("still renders the wall clock when there is no reading", () => {
    expect(
      renderClockElementValue(
        el({ type: "time", format: "24h" }),
        noReading,
        now
      )
    ).toBe("14:05");
  });

  it("renders time in the element's pinned format", () => {
    expect(
      renderClockElementValue(el({ type: "time", format: "24h" }), glucose, now)
    ).toBe("14:05");
  });

  it("renders explicit placeholders for insulin and carbs on board", () => {
    expect(renderClockElementValue(el({ type: "iob" }), glucose, now)).toBe(
      "--U"
    );
    expect(renderClockElementValue(el({ type: "cob" }), glucose, now)).toBe(
      "--g"
    );
  });

  it("renders nothing for element types with no runtime data source", () => {
    // A saved face may still contain these; they must not print a plausible number.
    for (const type of UNWIRED_ELEMENT_TYPES) {
      expect(renderClockElementValue(el({ type }), glucose, now)).toBe("");
    }
  });

  it("renders custom text and nothing for icon-rendered types", () => {
    expect(
      renderClockElementValue(el({ type: "text", text: "Hi" }), glucose, now)
    ).toBe("Hi");
    expect(renderClockElementValue(el({ type: "text" }), glucose, now)).toBe(
      ""
    );
    expect(renderClockElementValue(el({ type: "arrow" }), glucose, now)).toBe(
      ""
    );
    expect(renderClockElementValue(el({ type: "tracker" }), glucose, now)).toBe(
      ""
    );
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
