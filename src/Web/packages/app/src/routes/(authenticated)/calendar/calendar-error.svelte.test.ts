import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";

vi.mock("#lib/api/generated/statistics.generated.remote.js", () => ({
  getPunchCardData: () => remoteQueryError(new Error("The statistics service is down")),
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getDefinitions: () => remoteQuery(() => []),
  getActiveInstances: () => remoteQuery(() => []),
  getInstanceHistory: () => remoteQuery(() => []),
  completeInstance: remoteCommand(() => Promise.resolve()),
}));

vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));

import CalendarPage from "./CalendarPageHarness.test.svelte";

describe("calendar error view", () => {
  it("has one top-level heading, and it names the failure", async () => {
    render(CalendarPage, {});

    await expect.element(page.getByRole("heading", { level: 1, name: "Could not load the calendar" })).toBeVisible();
    expect(page.getByRole("heading", { level: 1 }).elements()).toHaveLength(1);
  });
});
