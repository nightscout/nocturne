import { render } from "vitest-browser-svelte";
import { describe, it, expect, afterEach } from "vitest";
import type { ClockSettings } from "$lib/api";
import { glucoseUnits, timeFormat } from "$lib/stores/appearance-store.svelte";
import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
import { renderClockElementValue } from "$lib/components/clock/element-value";
import {
  ELEMENT_GROUPS,
  elementInfo,
  type ClockElementType,
} from "$lib/clock-builder";
import ClockElementPreview from "./ClockElementPreview.svelte";
import Harness from "./ClockElementPreviewHarness.test.svelte";

const now = new Date(2026, 11, 31, 14, 5);

const glucose = {
  currentBG: 120,
  bgDelta: 5,
  direction: "Flat",
  lastUpdated: now.getTime() - 7 * 60_000,
  demoMode: false,
};

const noReading = {
  currentBG: null,
  bgDelta: 0,
  direction: "",
  lastUpdated: null,
  demoMode: false,
};

const mgdl12: ClockSettings = { glucoseUnits: "mg/dl", timeFormat: "12" };
const mmol24: ClockSettings = { glucoseUnits: "mmol", timeFormat: "24" };

function preview(
  type: ClockElementType,
  settings: ClockSettings = mgdl12,
  source: ClockGlucoseSource = glucose
) {
  const { container } = render(ClockElementPreview, {
    element: { _id: type, type },
    settings,
    glucose: source,
    now,
    trackerDefinitions: [],
  });
  return container.textContent?.trim() ?? "";
}

afterEach(() => {
  glucoseUnits.current = "mg/dl";
  timeFormat.current = "12";
});

describe("ClockElementPreview", () => {
  it("renders the preview in the face's units and time format, not the editor's", () => {
    glucoseUnits.current = "mg/dl";
    timeFormat.current = "12";
    expect(preview("sg", mmol24)).toBe("6.7 mmol/L");
    expect(preview("delta", mmol24)).toBe("+0.3 mmol/L");
    expect(preview("time", mmol24)).toBe("14:05");

    glucoseUnits.current = "mmol";
    timeFormat.current = "24";
    expect(preview("sg", mgdl12)).toBe("120 mg/dL");
    expect(preview("delta", mgdl12)).toBe("+5 mg/dL");
  });

  it.each([mgdl12, mmol24])(
    "shows what the saved face will show, in $glucoseUnits",
    (settings) => {
      // Every type the picker offers, so a newly wired element is covered too.
      for (const type of ELEMENT_GROUPS.flatMap((group) => group.types)) {
        // Icon-only and chart elements have their own branch and no value text.
        if (type === "arrow" || type === "tracker" || type === "chart") continue;
        const value = renderClockElementValue(
          { type },
          settings,
          glucose,
          now
        );
        // An element the runtime shows nothing for is named, never given a value.
        expect(preview(type, settings), type).toBe(
          value || elementInfo(type)?.name
        );
      }
    }
  );

  it("names the elements the runtime renderer has no value for", () => {
    expect(preview("summary")).toBe("Summary");
    expect(preview("trackers")).toBe("Trackers");
  });

  it("shows the no-reading face, not a fabricated one, with no reading", () => {
    expect(preview("sg", mgdl12, noReading)).toBe("--");
    // No value to show, so the builder names the element instead.
    expect(preview("delta", mgdl12, noReading)).toBe(
      elementInfo("delta")?.name
    );
    expect(preview("age", mgdl12, noReading)).toBe(elementInfo("age")?.name);
  });

  it("follows the glucose source after mount", async () => {
    let setGlucose!: (next: ClockGlucoseSource) => void;
    const { container } = render(Harness, {
      props: {
        element: { _id: "sg", type: "sg" },
        initialGlucose: glucose,
        now,
        onready: (set) => (setGlucose = set),
      },
    });

    expect(container.textContent?.trim()).toBe("120 mg/dL");
    setGlucose({ ...glucose, currentBG: 87 });
    await expect.poll(() => container.textContent?.trim()).toBe("87 mg/dL");
  });
});
