import { render } from "vitest-browser-svelte";
import { describe, it, expect, afterEach } from "vitest";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
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

function preview(type: ClockElementType, source: ClockGlucoseSource = glucose) {
  const { container } = render(ClockElementPreview, {
    element: { _id: type, type, format: "24h" },
    glucose: source,
    now,
    trackerDefinitions: [],
  });
  return container.textContent?.trim() ?? "";
}

afterEach(() => {
  glucoseUnits.current = "mg/dl";
});

describe("ClockElementPreview", () => {
  it("renders the preview glucose in the tenant's units", () => {
    glucoseUnits.current = "mmol";
    expect(preview("sg")).toBe("6.7");
    expect(preview("delta")).toBe("+0.3 mmol/L");

    glucoseUnits.current = "mg/dl";
    expect(preview("sg")).toBe("120");
    expect(preview("delta")).toBe("+5 mg/dL");
  });

  it.each(["mg/dl", "mmol"] as const)(
    "shows what the saved face will show, in %s",
    (units) => {
      glucoseUnits.current = units;
      // Every type the picker offers, so a newly wired element is covered too.
      for (const type of ELEMENT_GROUPS.flatMap((group) => group.types)) {
        // Icon-only and chart elements have their own branch and no value text.
        if (type === "arrow" || type === "tracker" || type === "chart") continue;
        const value = renderClockElementValue(
          { type, format: "24h" },
          glucose,
          now
        );
        // An element the runtime shows nothing for is named, never given a value.
        expect(preview(type), type).toBe(value || elementInfo(type)?.name);
      }
    }
  );

  it("names the elements the runtime renderer has no value for", () => {
    expect(preview("summary")).toBe("Summary");
    expect(preview("trackers")).toBe("Trackers");
  });

  it("shows the no-reading face, not a fabricated one, with no reading", () => {
    expect(preview("sg", noReading)).toBe("--");
    // No value to show, so the builder names the element instead.
    expect(preview("delta", noReading)).toBe(elementInfo("delta")?.name);
    expect(preview("age", noReading)).toBe(elementInfo("age")?.name);
  });

  it("follows the glucose source after mount", async () => {
    glucoseUnits.current = "mg/dl";
    let setGlucose!: (next: ClockGlucoseSource) => void;
    const { container } = render(Harness, {
      props: {
        element: { _id: "sg", type: "sg" },
        initialGlucose: glucose,
        now,
        onready: (set) => (setGlucose = set),
      },
    });

    expect(container.textContent?.trim()).toBe("120");
    setGlucose({ ...glucose, currentBG: 87 });
    await expect.poll(() => container.textContent?.trim()).toBe("87");
  });
});
