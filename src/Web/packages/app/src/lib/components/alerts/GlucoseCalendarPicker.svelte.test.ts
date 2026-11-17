import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const punchCard = vi.hoisted(() => ({ response: undefined as unknown }));

vi.mock("#lib/api/generated/statistics.generated.remote.js", () => ({
  getPunchCardData: () => remoteQuery(() => punchCard.response),
}));

import GlucoseCalendarPicker from "./GlucoseCalendarPicker.svelte";

const NOTE = /cannot show each day's glucose trace/;

describe("GlucoseCalendarPicker", () => {
  beforeEach(() => {
    punchCard.response = undefined;
  });

  it("shows the time zone note when the punch card carries a reason", async () => {
    punchCard.response = {
      months: [],
      timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
    };
    render(GlucoseCalendarPicker);

    await expect.element(page.getByText(NOTE)).toBeVisible();
  });

  it("shows no note when the punch card has no reason", async () => {
    punchCard.response = { months: [] };
    render(GlucoseCalendarPicker);

    await expect.element(page.getByLabelText("Next month")).toBeVisible();
    await expect.element(page.getByText(NOTE)).not.toBeInTheDocument();
  });

  it("fits a 360px viewport", async () => {
    punchCard.response = { months: [] };
    const original = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(360, 800);
    try {
      const screen = render(GlucoseCalendarPicker);
      await expect.element(page.getByLabelText("Next month")).toBeVisible();

      const box = screen.container.firstElementChild!.getBoundingClientRect();
      expect(box.left).toBeGreaterThanOrEqual(0);
      expect(box.right).toBeLessThanOrEqual(360);
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(360);
    } finally {
      await page.viewport(original[0], original[1]);
    }
  });
});
