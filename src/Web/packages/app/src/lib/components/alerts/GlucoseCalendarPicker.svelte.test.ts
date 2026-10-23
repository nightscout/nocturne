import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "$lib/api";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

const punchCard = vi.hoisted(() => ({ response: undefined as unknown }));

vi.mock("$api/generated/statistics.generated.remote", () => ({
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
});
