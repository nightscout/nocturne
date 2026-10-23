import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, onTestFinished, vi } from "vitest";
import type { Component } from "svelte";
import type { CoachMarkContext, MarkState } from "@nocturne/coach";
import { page as pageState } from "$app/state";
import { coachCopy, tourReplayLabel } from "$lib/coach-marks/copy.svelte";
import { loopAnalysisTour, sequences } from "$lib/coach-marks/sequences";
import { ALL_SCOPES, profileResponse } from "$lib/components/reports/loop-analysis/daily-profile.test-fixtures";
import { eventResponse } from "$lib/components/reports/loop-analysis/event-response.test-fixtures";
import { predictionResponse } from "$lib/components/reports/loop-analysis/prediction-accuracy.test-fixtures";
import { loopHealthResponse } from "$lib/components/reports/loop-analysis/loop-health.test-fixtures";
import {
  settingsEvidenceResponse,
  WITHHELD_SETTINGS_EVIDENCE,
} from "$lib/components/reports/loop-analysis/settings-evidence.test-fixtures";
import { LoopAnalysisSeries, SettingsEvidenceWithheldReason } from "$lib/api";
import {
  LOOP_ANALYSIS_PAGES,
  type LoopAnalysisPage,
} from "$lib/components/reports/loop-analysis/loop-analysis-pages.svelte";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import Harness from "./LoopAnalysisTourHarness.test.svelte";

vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("$lib/test-stubs/reactive-fields.svelte");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});

/** A test's own response for a page, served in place of the full one. */
const served = vi.hoisted(() => new Map<string, () => unknown>());

vi.mock("$api/generated/loopAnalysis.generated.remote", () => ({
  getEnvelope: () => remoteQuery(() => ({ ...profileResponse(), profileNames: ["Default"] })),
  getDailyProfile: () => remoteQuery(served.get("daily-profile") ?? profileResponse),
  getEventResponse: () => remoteQuery(served.get("meals-and-events") ?? eventResponse),
  getPredictionAccuracy: () => remoteQuery(served.get("prediction-accuracy") ?? predictionResponse),
  getLoopHealth: () => remoteQuery(served.get("loop-health") ?? loopHealthResponse),
  getSettingsEvidence: () => remoteQuery(served.get("settings-evidence") ?? settingsEvidenceResponse),
}));

vi.mock("$app/navigation", () => ({ goto: () => Promise.resolve() }));

vi.mock("$lib/hooks/date-params.svelte", () => ({
  requireDateParamsContext: () => ({ fromDay: "2026-10-09", toDay: "2026-10-22" }),
}));

vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: <T>(create: () => { current: T | undefined }) => ({
    get current() {
      return create().current;
    },
    stale: false,
    error: null,
  }),
}));

const PAGES: Record<LoopAnalysisPage, () => Promise<{ default: Component }>> = {
  "daily-profile": () => import("./daily-profile/+page.svelte"),
  "meals-and-events": () => import("./meals-and-events/+page.svelte"),
  "prediction-accuracy": () => import("./prediction-accuracy/+page.svelte"),
  "loop-health": () => import("./loop-health/+page.svelte"),
  "settings-evidence": () => import("./settings-evidence/+page.svelte"),
};

const SHARED = sequences["loop-analysis-pages"].steps;
const steps = (name: LoopAnalysisPage) => sequences[loopAnalysisTour(name)].steps;
const title = (key: string) => (coachCopy() as Record<string, { title: string }>)[key].title;

async function open(name: LoopAnalysisPage, options: { store?: Map<string, MarkState>; data?: Record<string, unknown> } = {}) {
  pageState.url = new URL(`http://localhost/reports/loop-analysis/${name}`) as typeof pageState.url;
  for (const key of Object.keys(pageState.data)) delete pageState.data[key];
  Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" }, ...options.data });

  let ctx: CoachMarkContext | undefined;
  const store = options.store ?? new Map<string, MarkState>();
  const screen = render(Harness, {
    report: (await PAGES[name]()).default,
    store,
    oncontext: (c: CoachMarkContext) => (ctx = c),
  });
  await vi.waitFor(() => expect(ctx).toBeDefined());
  return { ctx: ctx!, store, screen };
}

/**
 * Reads each step the tour raises and closes it with Done. The overlay waits for its target to be
 * on screen, so each target is scrolled to first, as a reader would.
 */
async function walk(ctx: CoachMarkContext): Promise<string[]> {
  await vi.waitFor(() => expect(ctx.activeKey).not.toBeNull(), { timeout: 3_000 });
  const shown: string[] = [];
  for (let key = ctx.activeKey; key && shown.length < 10; key = ctx.activeKey) {
    ctx.getMountedSteps(key)[0].element.scrollIntoView({ block: "center" });
    await expect.element(page.getByRole("dialog", { name: title(key) })).toBeVisible();
    shown.push(key);
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await vi.waitFor(() => expect(ctx.activeKey).not.toBe(key));
  }
  return shown;
}

/** Reads each step the tour raises and closes it with Done, without scrolling the page. */
async function walkInPlace(ctx: CoachMarkContext): Promise<string[]> {
  await vi.waitFor(() => expect(ctx.activeKey).not.toBeNull(), { timeout: 3_000 });
  const shown: string[] = [];
  for (let key = ctx.activeKey; key && shown.length < 10; key = ctx.activeKey) {
    const dialog = page.getByRole("dialog", { name: title(key) });
    try {
      await expect.element(dialog, { timeout: 1_500 }).toBeVisible();
    } catch {
      break;
    }
    shown.push(key);
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await vi.waitFor(() => expect(ctx.activeKey).not.toBe(key));
  }
  return shown;
}

describe("Loop Analysis tours", () => {
  // An overlay holds a history entry until it closes; let the previous test's go first.
  beforeEach(async () => {
    served.clear();
    await vi.waitFor(() => expect(history.state?.__coachMark).not.toBe(true), { timeout: 5_000 });
  });

  it.each(LOOP_ANALYSIS_PAGES)("tours %s on a first visit, after the pages and filters", async (name) => {
    const { ctx } = await open(name);

    expect(await walk(ctx)).toEqual([...SHARED, ...steps(name)]);
  });

  it("tours the daily profile on a desktop screen without the reader scrolling", async () => {
    const [width, height] = [window.innerWidth, window.innerHeight];
    onTestFinished(() => page.viewport(width, height));
    await page.viewport(1280, 800);
    const { ctx } = await open("daily-profile");

    expect(await walkInPlace(ctx)).toEqual([...SHARED, ...steps("daily-profile")]);
  });

  it("explains the pages and filters on the first page seen only", async () => {
    const first = await open("daily-profile");
    await walk(first.ctx);
    first.screen.unmount();

    const later = await open("loop-health", { store: first.store });

    expect(await walk(later.ctx)).toEqual(steps("loop-health"));
  });

  it("does not start a page's tour again once it is finished", async () => {
    const first = await open("prediction-accuracy");
    await walk(first.ctx);
    first.screen.unmount();
    const last = steps("prediction-accuracy").at(-1)!;
    first.store.delete(last);

    const later = await open("prediction-accuracy", { store: first.store });

    expect(await walk(later.ctx)).toEqual([last]);
  });

  it("replays the pages and filters, then the page's tour, from the button", async () => {
    const { ctx } = await open("settings-evidence");
    await walk(ctx);

    await page.getByRole("button", { name: tourReplayLabel() }).click();

    expect(await walk(ctx)).toEqual([...SHARED, ...steps("settings-evidence")]);
  });

  it("holds back the page's tour when the reader dismisses the pages and filters", async () => {
    const { ctx, store } = await open("daily-profile");
    await vi.waitFor(() => expect(ctx.activeKey).toBe(SHARED[0]), { timeout: 3_000 });
    await expect.element(page.getByRole("dialog", { name: title(SHARED[0]) })).toBeVisible();

    const active: string[] = [];
    const stop = $effect.root(() => {
      $effect(() => {
        if (ctx.activeKey) active.push(ctx.activeKey);
      });
    });
    await page.getByRole("button", { name: "Dismiss" }).click();
    await vi.waitFor(() => expect(store.get(SHARED[0])?.status).toBe("dismissed"));

    // The replay raising the first step again shows the queue ran after the dismiss.
    await page.getByRole("button", { name: tourReplayLabel() }).click();
    await vi.waitFor(() => expect(ctx.activeKey).toBe(SHARED[0]));
    stop();

    expect(active.filter((key) => steps("daily-profile").includes(key))).toEqual([]);
    for (const key of steps("daily-profile")) expect(store.get(key)?.status ?? "unseen", key).toBe("unseen");
  });

  it("tours the next page when the reader moves to it in the middle of a tour", async () => {
    const { ctx, screen } = await open("daily-profile");
    await vi.waitFor(() => expect(ctx.activeKey).toBe(SHARED[0]), { timeout: 3_000 });
    await expect.element(page.getByRole("dialog", { name: title(SHARED[0]) })).toBeVisible();
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await vi.waitFor(() => expect(ctx.activeKey).toBe(steps("daily-profile")[0]));

    pageState.url = new URL("http://localhost/reports/loop-analysis/loop-health") as typeof pageState.url;
    screen.rerender({ report: (await PAGES["loop-health"]()).default });
    await vi.waitFor(() => expect(steps("daily-profile")).not.toContain(ctx.activeKey));

    expect(await walk(ctx)).toEqual(steps("loop-health"));
  });

  it("tours a member limited to 24 hours", async () => {
    const { ctx } = await open("daily-profile", { data: { limitTo24Hours: true } });

    expect(await walk(ctx)).toEqual([...SHARED, ...steps("daily-profile")]);
  });

  it.each([
    [
      "the page withheld for a member limited to 24 hours",
      { limitTo24Hours: true },
      () =>
        settingsEvidenceResponse({
          historyClamped: true,
          withheldReason: SettingsEvidenceWithheldReason.HistoryClamped,
          primary: undefined,
        }),
    ],
    ["the page withheld from a reader without access", {}, () => settingsEvidenceResponse(WITHHELD_SETTINGS_EVIDENCE)],
  ])("does not tour settings evidence with %s", async (_, data, response) => {
    served.set("settings-evidence", response);
    const { ctx } = await open("settings-evidence", { data });

    expect(await walk(ctx)).toEqual(SHARED);
  });

  it("does not show the events step when the reader's access hides the events", async () => {
    served.set("meals-and-events", () => eventResponse({ withheld: [LoopAnalysisSeries.EventList] }));
    const { ctx } = await open("meals-and-events");

    expect(await walk(ctx)).toEqual([...SHARED, ...steps("meals-and-events").filter((key) => !key.endsWith(".events"))]);
  });

  it("does not show the contexts step when the reader's access hides the contexts", async () => {
    served.set("prediction-accuracy", () => predictionResponse({ scopes: ALL_SCOPES.filter((s) => s !== "treatments.read") }));
    const { ctx } = await open("prediction-accuracy");

    expect(await walk(ctx)).toEqual([...SHARED, ...steps("prediction-accuracy").filter((key) => !key.endsWith(".contexts"))]);
  });

  it.each([
    ["a share viewer", { user: null }],
    ["a guest", { isGuestSession: true }],
  ])("leaves the page alone for %s", async (_, data) => {
    const { ctx } = await open("daily-profile", { data });
    await expect.element(page.getByTestId("loop-analysis-daily-totals")).toBeVisible();

    for (const key of [...SHARED, ...steps("daily-profile")]) expect(ctx.getMountedSteps(key), key).toHaveLength(0);
    expect(ctx.activeKey).toBeNull();
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: tourReplayLabel() })).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Show tip" })).not.toBeInTheDocument();
  });
});
