import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Component } from "svelte";
import { page as pageState } from "$app/state";
import {
  EventAnchorKind,
  type DailyProfileResponse,
  type EventResponseReport,
  type LoopAnalysisResponse,
  type LoopHealthResponse,
  type PredictionAccuracyResponse,
  type SettingsEvidenceReport,
} from "#lib/api/index.js";
import { releaseWatercolour, resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { ALL_SCOPES, profileResponse } from "#lib/components/reports/loop-analysis/daily-profile.test-fixtures.js";
import { eventResponse } from "#lib/components/reports/loop-analysis/event-response.test-fixtures.js";
import { predictionResponse } from "#lib/components/reports/loop-analysis/prediction-accuracy.test-fixtures.js";
import { loopHealthResponse } from "#lib/components/reports/loop-analysis/loop-health.test-fixtures.js";
import { settingsEvidenceResponse } from "#lib/components/reports/loop-analysis/settings-evidence.test-fixtures.js";
import Harness from "./LoopAnalysisHarness.test.svelte";

const calls = vi.hoisted(() => ({
  /** Every query each remote function was asked for, by page, or `envelope`. */
  fetched: {} as Record<string, unknown[]>,
  goto: [] as [string, unknown][],
  /** The error title of each resource created, in order. */
  created: [] as string[],
  /** Whether a goto moves the page's URL, as SvelteKit's does, rather than only being recorded. */
  navigate: false,
}));

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

// The pages read `page.url` reactively; the shared stub's is a plain property.
vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});

let envelope: LoopAnalysisResponse;
let daily: DailyProfileResponse;
let events: EventResponseReport | undefined;
/** The Meals and events response for each query, when `events` is undefined. */
let eventsFor: ((query: Record<string, unknown>) => EventResponseReport) | undefined;
let predictions: PredictionAccuracyResponse | undefined;
let health: LoopHealthResponse;
let evidence: SettingsEvidenceReport;
let stale = false;

vi.mock("#lib/api/generated/loopAnalysis.generated.remote.js", () => {
  const remote = (name: string, read: (input: unknown) => unknown) => (input: unknown) => {
    (calls.fetched[name] ??= []).push(input);
    return remoteQuery(() => read(input));
  };
  return {
    getEnvelope: remote("envelope", () => envelope),
    getDailyProfile: remote("daily-profile", () => daily),
    getEventResponse: remote("meals-and-events", (input) => events ?? eventsFor!(input as Record<string, unknown>)),
    getPredictionAccuracy: remote("prediction-accuracy", () => predictions),
    getLoopHealth: remote("loop-health", () => health),
    getSettingsEvidence: remote("settings-evidence", () => evidence),
  };
});

vi.mock("$app/navigation", async () => {
  const stub = await vi.importActual<typeof import("$app/navigation")>("$app/navigation");
  const { page: state } = await import("$app/state");
  return {
    ...stub,
    goto: (url: string, options?: unknown) => {
      calls.goto.push([url, options]);
      if (calls.navigate) state.url = new URL(url, "http://localhost") as typeof state.url;
      return Promise.resolve();
    },
  };
});

// The reports layout's real range seeds the URL with a goto, which these tests count.
vi.mock("#lib/hooks/date-params.svelte.js", async (importOriginal) => {
  const range = { fromDay: "2026-10-09", toDay: "2026-10-22" };
  return {
    ...(await importOriginal<typeof import("#lib/hooks/date-params.svelte.js")>()),
    useDateParams: () => range,
    setDateParamsContext: () => {},
    createSharedRangeUse: () => ({ consumed: false }),
    requireDateParamsContext: () => range,
  };
});

vi.mock("#lib/hooks/resource-context.svelte.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("#lib/hooks/resource-context.svelte.js")>()),
  contextResource: <T>(create: () => { current: T | undefined }, options?: { errorTitle?: string }) => {
    calls.created.push(options?.errorTitle ?? "");
    return {
      get current() {
        return create().current;
      },
      get stale() {
        return stale;
      },
      error: null,
    };
  },
}));

const PAGES = {
  "daily-profile": () => import("./daily-profile/+page.svelte"),
  "meals-and-events": () => import("./meals-and-events/+page.svelte"),
  "prediction-accuracy": () => import("./prediction-accuracy/+page.svelte"),
  "loop-health": () => import("./loop-health/+page.svelte"),
  "settings-evidence": () => import("./settings-evidence/+page.svelte"),
};
type PageName = keyof typeof PAGES;
const PAGE_NAMES: PageName[] = [
  "daily-profile",
  "meals-and-events",
  "prediction-accuracy",
  "loop-health",
  "settings-evidence",
];

const ENVELOPE_ERROR = "Cannot load Loop analysis";

async function report(name: PageName): Promise<Component> {
  return (await PAGES[name]()).default;
}

async function open(name: PageName, search = "", data: Record<string, unknown> = {}) {
  pageState.url = new URL(`http://localhost/reports/loop-analysis/${name}${search}`) as typeof pageState.url;
  for (const key of Object.keys(pageState.data)) delete pageState.data[key];
  Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" }, ...data });
  return render(Harness, { report: await report(name) });
}

/** Follows a page link as SvelteKit would: the URL changes and the layout stays. */
async function follow(screen: Awaited<ReturnType<typeof open>>, name: string) {
  const href = switcher().getByRole("link", { name }).element().getAttribute("href")!;
  const url = new URL(href, "http://localhost");
  pageState.url = url as typeof pageState.url;
  const target = PAGE_NAMES.find((p) => url.pathname.endsWith(p))!;
  await screen.rerender({ report: await report(target) });
}

function lastGoto() {
  const target = calls.goto.at(-1);
  expect(target).toBeDefined();
  return { url: new URL(target![0], "http://localhost"), options: target![1] };
}

const switcher = () => page.getByRole("navigation", { name: "Loop Analysis pages" });
const hrefOf = (link: ReturnType<typeof page.getByRole>) =>
  new URL(link.element().getAttribute("href")!, "http://localhost");
/** The element a pointer at the centre of the given one lands on. */
function hit(element: Element) {
  const box = element.getBoundingClientRect();
  return document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
}
const distinct = (list: unknown[] = []) => new Set(list.map((q) => JSON.stringify(q)));

describe("Loop Analysis pages", () => {
  afterEach(() => resetWatercolour());

  beforeEach(() => {
    calls.fetched = {};
    calls.goto.length = 0;
    calls.created.length = 0;
    calls.navigate = false;
    envelope = { ...profileResponse(), profileNames: ["Default"] };
    daily = profileResponse();
    events = eventResponse();
    eventsFor = undefined;
    predictions = predictionResponse();
    health = loopHealthResponse();
    evidence = settingsEvidenceResponse();
    stale = false;
  });

  it.each(PAGE_NAMES)("%s fetches the shared envelope and only its own data, with the shared filters", async (name) => {
    await open(name, "?days=14&weekdays=6&excludeIllness=false&compare=previous");

    await expect.element(switcher()).toBeVisible();
    expect(Object.keys(calls.fetched).sort()).toEqual(["envelope", name].sort());
    for (const asked of [calls.fetched.envelope.at(-1), calls.fetched[name].at(-1)])
      expect(asked).toMatchObject({
        startDate: "2026-10-09",
        endDate: "2026-10-22",
        days: [6],
        excludeIllness: false,
        compare: "Previous",
      });
    expect(calls.created).toEqual([ENVELOPE_ERROR, expect.not.stringMatching(ENVELOPE_ERROR)]);
  });

  it("links every page under the filters on screen, marking the one open", async () => {
    await open("loop-health", "?days=30&weekdays=6&excludeExercise=true");
    releaseWatercolour();
    await vi.waitFor(() => expect(document.querySelector("[data-report-root] canvas")).toBeTruthy());

    const labels = ["Daily profile", "Meals and events", "Prediction accuracy", "Loop health", "Settings evidence"];
    for (const [i, name] of labels.entries()) {
      const href = hrefOf(switcher().getByRole("link", { name }));
      expect(href.pathname).toBe(`/reports/loop-analysis/${PAGE_NAMES[i]}`);
      expect(href.searchParams.get("days")).toBe("30");
      expect(href.searchParams.get("weekdays")).toBe("6");
      expect(href.searchParams.get("excludeExercise")).toBe("true");
      const link = switcher().getByRole("link", { name }).element();
      expect(link.contains(hit(link)), name).toBe(true);
    }
    await expect.element(switcher().getByRole("link", { name: "Loop health" })).toHaveAttribute("aria-current", "page");
    await expect.element(switcher().getByRole("link", { name: "Daily profile" })).not.toHaveAttribute("aria-current");
  });

  it("keeps the filters and the envelope across a move to another page, and loads only that page", async () => {
    const screen = await open("daily-profile", "?days=14&weekdays=6&excludeExercise=true&profile=Default");
    await expect.element(page.getByRole("slider", { name: "Glucose", exact: true })).toBeVisible();

    await follow(screen, "Prediction accuracy");
    await expect.element(page.getByRole("slider", { name: "UAM", exact: true })).toBeVisible();

    expect(calls.created.filter((title) => title === ENVELOPE_ERROR)).toHaveLength(1);
    expect(distinct(calls.fetched.envelope).size).toBe(1);
    expect(calls.fetched["prediction-accuracy"].at(-1)).toMatchObject({
      days: [6],
      excludeExercise: true,
      profileName: "Default",
    });
    await page.getByRole("button", { name: "Open filters" }).click();
    await expect.element(page.getByLabelText("Exercise")).toBeChecked();
  });

  it("applies a filter set on one page to the next page opened", async () => {
    calls.navigate = true;
    const screen = await open("loop-health", "?days=14");
    await page.getByRole("button", { name: "Open filters" }).click();
    await page.getByLabelText("Overrides").click();
    await page.getByRole("button", { name: "Apply filters" }).click();
    await expect.poll(() => pageState.url.searchParams.get("excludeOverrides")).toBe("true");

    await follow(screen, "Settings evidence");
    await expect.element(switcher().getByRole("link", { name: "Settings evidence" })).toHaveAttribute("aria-current", "page");
    expect(calls.fetched["settings-evidence"].at(-1)).toMatchObject({ excludeOverrides: true });
  });

  it("restores the filters from the URL a page is reloaded with", async () => {
    await open("settings-evidence", "?days=14&weekdays=6&excludeExercise=true&compare=previous");
    await page.getByRole("button", { name: "Open filters" }).click();

    await expect.element(page.getByLabelText("Exercise")).toBeChecked();
    await expect.element(page.getByRole("button", { name: "Sat" })).toHaveAttribute("aria-pressed", "true");
    expect(calls.fetched["settings-evidence"].at(-1)).toMatchObject({ days: [6], excludeExercise: true, compare: "Previous" });
  });

  it("shows a page's own loading below the shared parts while its first response loads", async () => {
    predictions = undefined;
    await open("prediction-accuracy");

    await expect.element(page.getByRole("status", { name: "Loading" })).toBeVisible();
    await expect.element(switcher()).toBeVisible();
    await expect.element(page.getByText(/compares the glucose predictions of the loop/)).toBeVisible();
  });

  it("opens the Filters panel on the filters in force, the API's defaults included", async () => {
    await open("daily-profile", "?excludeExercise=true");
    await page.getByRole("button", { name: "Open filters" }).click();

    await expect.element(page.getByLabelText("Exercise")).toBeChecked();
    await expect.element(page.getByLabelText("Illness")).toBeChecked();
    await expect.element(page.getByLabelText("Overrides")).not.toBeChecked();
  });

  it("holds the masks the URL leaves unset until the envelope says what they default to", async () => {
    stale = true;
    await open("daily-profile", "?excludeExercise=true");
    await page.getByRole("button", { name: "Open filters" }).click();

    await expect.element(page.getByLabelText("Exercise")).toBeChecked();
    await expect.element(page.getByLabelText("Exercise")).toBeEnabled();
    await expect.element(page.getByLabelText("Illness")).toBeDisabled();
    await expect.element(page.getByRole("group", { name: "Exclude the time of" })).toHaveAttribute("aria-busy", "true");
  });

  it("links the pages the viewer can load from a page it cannot list", async () => {
    await open("settings-evidence", "?days=14", {
      effectivePermissions: ["glucose.read", "treatments.read", "devices.read", "reports.read"],
    });

    await expect.element(switcher().getByRole("link", { name: "Loop health" })).toBeVisible();
    expect(switcher().getByRole("link", { name: "Settings evidence" }).elements()).toHaveLength(0);
  });

  it("writes the panel's filters into this page's URL, leaving out an untouched default", async () => {
    await open("prediction-accuracy", "?days=14&isDefault=false&weekdays=6");
    await page.getByRole("button", { name: "Open filters" }).click();
    await page.getByLabelText("Overrides").click();
    await page.getByRole("button", { name: "Apply filters" }).click();

    const { url } = lastGoto();
    expect(url.pathname).toBe("/reports/loop-analysis/prediction-accuracy");
    expect(url.searchParams.get("excludeOverrides")).toBe("true");
    expect(url.searchParams.has("excludeIllness")).toBe(false);
    expect(url.searchParams.get("days")).toBe("14");
    expect(url.searchParams.get("weekdays")).toBe("6");
  });

  it("clears every Loop Analysis filter on Reset and keeps the range", async () => {
    await open("loop-health", "?days=30&weekdays=6&excludeExercise=true&compare=previous&profile=Default");
    await page.getByRole("button", { name: "Reset filters" }).click();

    const { url } = lastGoto();
    expect(url.pathname).toBe("/reports/loop-analysis/loop-health");
    expect([...url.searchParams]).toEqual([["days", "30"]]);
  });

  it("offers the profiles the envelope names", async () => {
    envelope = { ...envelope, profileNames: ["Default", "Weekend"] };
    await open("prediction-accuracy");
    await page.getByRole("button", { name: "Open filters" }).click();

    await page.elementLocator(document.getElementById("loop-analysis-profile")!).click();
    await expect.element(page.getByRole("option", { name: "Weekend" })).toBeVisible();
  });

  it("drops from the URL, replacing it, a filter the viewer cannot use", async () => {
    envelope = profileResponse({ scopes: ["glucose.read", "treatments.read", "therapy.read", "reports.read"] });
    await open("daily-profile", "?days=14&excludeOpenLoop=true&excludeIllness=false");

    await expect.poll(() => calls.goto.length).toBeGreaterThan(0);
    const { url, options } = lastGoto();
    expect(url.pathname).toBe("/reports/loop-analysis/daily-profile");
    expect(url.searchParams.has("excludeOpenLoop")).toBe(false);
    expect(url.searchParams.get("excludeIllness")).toBe("false");
    expect(options).toMatchObject({ replace: true });
  });

  it("leaves a URL alone when every filter it sets is usable", async () => {
    await open("daily-profile", "?days=14&excludeIllness=false");
    await expect.element(switcher()).toBeVisible();
    expect(calls.goto).toHaveLength(0);
  });

  it("names the compared range and a range the API shortened", async () => {
    envelope = {
      ...profileResponse({ comparison: true }),
      comparisonRange: { startDate: "2026-05-06", endDate: "2026-08-03", shortened: true },
    };
    await open("daily-profile", "?compare=previous");

    await expect.element(page.getByText(/Compared with May 6 – Aug 3, 2026/)).toBeVisible();
    await expect.element(page.getByText(/uses a maximum of 90 days/)).toBeVisible();
  });

  it("does not name a range from an envelope that answers an earlier filter", async () => {
    envelope = {
      ...profileResponse({ comparison: true }),
      comparisonRange: { startDate: "2026-05-06", endDate: "2026-08-03", shortened: true },
    };
    stale = true;
    await open("daily-profile", "?compare=previous");

    await expect.element(switcher()).toBeVisible();
    expect(page.getByText(/Compared with/).elements()).toHaveLength(0);
    expect(page.getByText(/uses a maximum of 90 days/).elements()).toHaveLength(0);
  });

  it("does not let a clamped viewer compare", async () => {
    envelope = profileResponse({ historyClamped: true });
    await open("daily-profile", "?compare=previous", { limitTo24Hours: true });
    await page.getByRole("button", { name: "Open filters" }).click();

    await expect.element(page.getByText(/You cannot compare, because you can see only the most recent history/)).toBeVisible();
    await expect.element(page.elementLocator(document.getElementById("loop-analysis-compare")!)).toBeDisabled();
  });

  it("links Daily profile to Insulin Delivery over the same range only", async () => {
    await open("daily-profile", "?days=30&isDefault=false&weekdays=6");

    const href = hrefOf(page.getByRole("link", { name: /Insulin Delivery/ }));
    expect(href.pathname).toBe("/reports/insulin-delivery");
    expect([...href.searchParams]).toEqual([
      ["days", "30"],
      ["isDefault", "false"],
    ]);
  });

  it("links Settings evidence's ISF to Loop health's sensitivity ratio under the same filters", async () => {
    await open("settings-evidence", "?weekdays=6&excludeIllness=false");

    const link = page.getByRole("link", { name: /sensitivity ratio by hour/ });
    await expect.element(link).toBeVisible();
    const href = hrefOf(link);
    expect(href.pathname).toBe("/reports/loop-analysis/loop-health");
    expect(href.hash).toBe("#sensitivity");
    expect(href.searchParams.get("weekdays")).toBe("6");
    expect(href.searchParams.get("excludeIllness")).toBe("false");
  });

  it("asks Meals and events for the cohorts in its URL", async () => {
    await open("meals-and-events", "?anchor1=Meal&fromMinute1=300&toMinute1=600&anchor2=UnannouncedRise&minutesAfter=480");

    await expect.element(page.getByLabelText("Cohort 1 from")).toHaveValue("05:00");
    expect(calls.fetched["meals-and-events"].at(-1)).toEqual({
      startDate: "2026-10-09",
      endDate: "2026-10-22",
      anchor1: EventAnchorKind.Meal,
      fromMinute1: 300,
      toMinute1: 600,
      anchor2: EventAnchorKind.UnannouncedRise,
      minutesAfter: 480,
    });
    expect(calls.fetched.envelope.at(-1)).toEqual({ startDate: "2026-10-09", endDate: "2026-10-22" });
  });

  it("writes a cohort change into the Meals and events URL beside the shared filters", async () => {
    await open("meals-and-events", "?days=14&excludeExercise=true");

    await page.getByLabelText("Isolated events only").click();
    const { url } = lastGoto();
    expect(url.pathname).toBe("/reports/loop-analysis/meals-and-events");
    expect(url.searchParams.get("isolatedOnly")).toBe("true");
    expect(url.searchParams.get("excludeExercise")).toBe("true");
    expect(url.searchParams.get("days")).toBe("14");
  });

  it("keeps naming a window outside the limits after the URL drops it", async () => {
    calls.navigate = true;
    events = undefined;
    eventsFor = (query) => ({ ...eventResponse(), ignoredFilters: query.minutesAfter === 500 ? ["minutesAfter"] : [] });
    await open("meals-and-events", "?anchor1=Meal&minutesAfter=500");

    await expect.poll(() => pageState.url.searchParams.has("minutesAfter")).toBe(false);
    expect(calls.fetched["meals-and-events"].at(-1)).not.toHaveProperty("minutesAfter");
    await expect.element(page.getByText(/These values are outside the limits/)).toBeVisible();
    await expect.element(page.getByText(/These values are outside the limits/)).toHaveTextContent(/After the event/);
  });

  it("drops from the URL a cohort whose events the viewer cannot read", async () => {
    events = {
      ...eventResponse(),
      unavailableAnchors: [{ anchor: EventAnchorKind.Meal, requiredScope: "treatments.read" }],
    };
    await open("meals-and-events", "?anchor1=Meal&anchor2=SensorStart");

    await expect.poll(() => calls.goto.length).toBeGreaterThan(0);
    const { url } = lastGoto();
    expect(url.searchParams.get("anchor1")).toBe("SensorStart");
    expect(url.searchParams.has("anchor2")).toBe(false);
  });
});
