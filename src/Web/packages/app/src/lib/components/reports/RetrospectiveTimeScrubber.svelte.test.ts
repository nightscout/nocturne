import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import RetrospectiveTimeScrubber from "./RetrospectiveTimeScrubber.svelte";
import { startOfDay, timeOnDay } from "$lib/utils/date-range";

// A zone well away from any the test browser is likely to run in, so a browser-zone reading shows.
const ZONE = "Pacific/Kiritimati";

describe("RetrospectiveTimeScrubber", () => {
  it("places the scrubber on the day's own clock in the zone it is given", async () => {
    render(RetrospectiveTimeScrubber, {
      props: {
        date: startOfDay("2026-10-13", ZONE),
        currentTime: timeOnDay("2026-10-13", "08:00", ZONE)!,
        timeZone: ZONE,
      },
    });

    await expect.element(page.getByRole("slider")).toHaveAttribute("aria-valuenow", "480");
    await expect.element(page.getByText(/^0?8:00/)).toBeVisible();
  });

  it("turns a scrubbed minute back into that wall-clock time in the zone", async () => {
    const onTimeChange = vi.fn();
    render(RetrospectiveTimeScrubber, {
      props: {
        date: startOfDay("2026-10-13", ZONE),
        currentTime: timeOnDay("2026-10-13", "08:00", ZONE)!,
        timeZone: ZONE,
        onTimeChange,
      },
    });

    await page.getByRole("slider").click();
    await expect.poll(() => onTimeChange.mock.calls.length).toBeGreaterThan(0);
    const picked: Date = onTimeChange.mock.calls.at(-1)![0];
    const minutes = Number(page.getByRole("slider").element().getAttribute("aria-valuenow"));
    expect(picked.getTime()).toBe(startOfDay("2026-10-13", ZONE).getTime() + minutes * 60_000);
  });

  it("steps the time with the arrow keys and jumps to midnight with Home", async () => {
    const onTimeChange = vi.fn();
    render(RetrospectiveTimeScrubber, {
      props: {
        date: startOfDay("2026-10-13", ZONE),
        currentTime: timeOnDay("2026-10-13", "08:00", ZONE)!,
        timeZone: ZONE,
        stepMinutes: 5,
        onTimeChange,
      },
    });
    const slider = page.getByRole("slider");

    await userEvent.keyboard("{ArrowRight}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "485");
    await userEvent.keyboard("{ArrowLeft}{ArrowLeft}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "475");
    await userEvent.keyboard("{Home}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "0");
    expect(onTimeChange.mock.calls.at(-1)![0].getTime()).toBe(startOfDay("2026-10-13", ZONE).getTime());
  });
});
