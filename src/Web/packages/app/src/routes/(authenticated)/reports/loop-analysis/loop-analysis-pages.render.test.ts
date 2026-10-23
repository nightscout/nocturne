import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "svelte/server";
import type { Component } from "svelte";
import { page as pageState } from "$app/state";
import { ALL_SCOPES } from "$lib/components/reports/loop-analysis/daily-profile.test-fixtures";

const calls = vi.hoisted(() => ({ fetched: [] as string[] }));

vi.mock("$api/generated/loopAnalysis.generated.remote", () => {
  const remote = (name: string) => () => {
    calls.fetched.push(name);
    return { loading: true, error: null, current: undefined, refresh: () => {} };
  };
  return {
    getEnvelope: remote("envelope"),
    getDailyProfile: remote("daily-profile"),
    getEventResponse: remote("meals-and-events"),
    getPredictionAccuracy: remote("prediction-accuracy"),
    getLoopHealth: remote("loop-health"),
    getSettingsEvidence: remote("settings-evidence"),
  };
});

// The pages' charts are not rendered without data, and their chart library does not load in node.
vi.mock("$lib/components/reports/loop-analysis/DailyProfileBody.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$lib/components/reports/loop-analysis/EventResponseBody.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$lib/components/reports/loop-analysis/EventCohortBuilder.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$lib/components/reports/loop-analysis/PredictionAccuracyBody.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$lib/components/reports/loop-analysis/LoopHealthBody.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$lib/components/reports/loop-analysis/SettingsEvidenceBody.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));

vi.mock("$app/paths", () => ({ resolve: (path: string) => path }));

vi.mock("$lib/hooks/date-params.svelte", () => ({
  requireDateParamsContext: () => ({ fromDay: "2026-10-09", toDay: "2026-10-22" }),
}));

vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: <T>(create: () => { current: T | undefined }) => {
    const query = create();
    return { current: query.current, stale: false, error: null };
  },
}));

import Harness from "./LoopAnalysisHarness.test.svelte";

const PAGES: Record<string, () => Promise<{ default: Component }>> = {
  "daily-profile": () => import("./daily-profile/+page.svelte"),
  "meals-and-events": () => import("./meals-and-events/+page.svelte"),
  "prediction-accuracy": () => import("./prediction-accuracy/+page.svelte"),
  "loop-health": () => import("./loop-health/+page.svelte"),
  "settings-evidence": () => import("./settings-evidence/+page.svelte"),
};

describe("Loop Analysis pages on the server", () => {
  beforeEach(() => {
    calls.fetched.length = 0;
  });

  it.each(Object.keys(PAGES))("%s fetches the envelope and its own data while rendering, and no other", async (name) => {
    pageState.url = new URL(`http://localhost/reports/loop-analysis/${name}?days=14`) as typeof pageState.url;
    Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" } });
    const { default: report } = await PAGES[name]();

    // Reading the body runs the render.
    const { body } = render(Harness, { props: { report } });

    expect(body).toContain(`aria-current="page"`);
    expect(body).toContain(`data-testid="report-pages"`);
    expect(calls.fetched).toEqual(["envelope", name]);
  });
});
