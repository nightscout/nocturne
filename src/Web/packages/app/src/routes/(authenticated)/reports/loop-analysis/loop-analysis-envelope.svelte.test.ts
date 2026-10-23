import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Component } from "svelte";
import { page as pageState } from "$app/state";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import { ALL_SCOPES, profileResponse } from "$lib/components/reports/loop-analysis/daily-profile.test-fixtures";
import { loopHealthResponse } from "$lib/components/reports/loop-analysis/loop-health.test-fixtures";
import Harness from "./LoopAnalysisHarness.test.svelte";

/** Every call of each remote function: with the real contextResource below, each is one request. */
const requests = vi.hoisted(() => ({ envelope: [] as unknown[], page: [] as unknown[] }));

vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("$lib/test-stubs/reactive-fields.svelte");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});

vi.mock("$api/generated/loopAnalysis.generated.remote", () => ({
  getEnvelope: (input: unknown) => {
    requests.envelope.push(input);
    return remoteQuery(() => profileResponse());
  },
  getDailyProfile: (input: unknown) => {
    requests.page.push(input);
    return remoteQuery(() => profileResponse());
  },
  getLoopHealth: (input: unknown) => {
    requests.page.push(input);
    return remoteQuery(() => loopHealthResponse());
  },
}));

vi.mock("$lib/hooks/date-params.svelte", () => ({
  requireDateParamsContext: () => ({ fromDay: "2026-10-09", toDay: "2026-10-22" }),
}));

describe("the Loop Analysis envelope", () => {
  beforeEach(() => {
    requests.envelope.length = 0;
    requests.page.length = 0;
    for (const key of Object.keys(pageState.data)) delete pageState.data[key];
    Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" } });
  });

  it("is requested once for a move between pages, which requests only the new page", async () => {
    pageState.url = new URL("http://localhost/reports/loop-analysis/daily-profile?days=14&weekdays=6") as typeof pageState.url;
    const daily: Component = (await import("./daily-profile/+page.svelte")).default;
    const health: Component = (await import("./loop-health/+page.svelte")).default;
    const screen = render(Harness, { report: daily });
    await expect.element(page.getByRole("slider", { name: "Glucose", exact: true })).toBeVisible();
    expect(requests.envelope).toHaveLength(1);
    const pagesBefore = requests.page.length;

    pageState.url = new URL("http://localhost/reports/loop-analysis/loop-health?days=14&weekdays=6") as typeof pageState.url;
    await screen.rerender({ report: health });
    await expect.element(page.getByRole("slider", { name: "Closed loop by hour" })).toBeVisible();

    expect(requests.envelope).toHaveLength(1);
    expect(requests.page.length).toBe(pagesBefore + 1);
    expect(requests.page.at(-1)).toEqual(requests.envelope[0]);
  });
});
