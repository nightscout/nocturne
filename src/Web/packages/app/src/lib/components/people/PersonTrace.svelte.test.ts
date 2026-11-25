import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import PersonTrace from "./PersonTrace.svelte";
import { makePerson, minutesAgo } from "./people-fixtures.js";

function traceOf(peak: number) {
  const mgdls = [peak, peak - 10, peak - 40, 250, 200, 150, 120, 100];
  return makePerson({
    recentReadings: mgdls.map((mgdl, i) => ({
      timestamp: minutesAgo(175 - i * 25),
      mgdl,
    })),
    thresholds: { urgentLow: 54, low: 70, high: 250, urgentHigh: 260 },
  });
}

async function drawnPaths(peak: number) {
  const { container } = render(PersonTrace, {
    props: { person: traceOf(peak), class: "h-10 w-64" },
  });
  const trace = page.getByTestId("person-trace");
  await expect.element(trace).toBeVisible();
  await expect
    .poll(() => container.querySelectorAll("path").length)
    .toBeGreaterThan(2);
  const top = trace.element().getBoundingClientRect().top;
  return [...container.querySelectorAll("path")]
    .filter((path) => path.getBoundingClientRect().height > 0)
    .map((path) => path.getBoundingClientRect().top - top);
}

describe("PersonTrace", () => {
  it.each([368, 450])(
    "keeps every reading inside the trace when the peak is %i mg/dL",
    async (peak) => {
      const offsets = await drawnPaths(peak);

      expect(offsets.length).toBeGreaterThan(0);
      for (const offset of offsets) {
        expect(offset).toBeGreaterThanOrEqual(0);
      }
    }
  );
});
