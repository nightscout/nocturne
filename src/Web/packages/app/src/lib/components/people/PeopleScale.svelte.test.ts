import { render } from "vitest-browser-svelte";
import { cdp, commands, page } from "vitest/browser";
import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import type {
  PeopleOverviewRequest,
  PeopleReviewRequest,
  TenantOverviewItem,
  TenantOverviewResponse,
  TenantReviewResponse,
} from "@nocturne/api-client";
import { answerOf, crowd, crowdIndex, crowdReview, nextPoll } from "./people-crowd-fixtures.js";

/** Each answer by the sort and search it was read for, as the remote function keeps one read per argument. */
let overviews = $state.raw<Record<string, TenantOverviewResponse>>({});
let reviews = $state.raw<Record<string, TenantReviewResponse>>({});

const poll = vi.hoisted(() => ({ tick: () => {} }));
/** The argument of the newest overview and review read the page asked for. */
const requested = vi.hoisted(() => ({ overview: "", review: "" }));
const nav = vi.hoisted(() => ({ address: new URL("http://localhost/") }));

function sortKey(args: PeopleOverviewRequest | PeopleReviewRequest) {
  const sort = args.query?.sort ?? null;
  return JSON.stringify(args.query?.search ? [sort, args.query.search] : sort);
}

vi.mock("$app/navigation", () => ({
  replaceState: (url: string | URL) => {
    nav.address = new URL(url, nav.address.href);
  },
  afterNavigate: () => {},
}));

vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({
  pollWhileVisible: (tick: () => void) => {
    poll.tick = tick;
  },
}));

vi.mock("#lib/hooks/now.svelte.js", () => ({
  Now: class {
    readonly current = Date.now();
  },
}));

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/peopleViews.generated.remote.js", () => ({
  getPeopleViews: () => ({
    current: { views: [], limit: 20 },
    error: undefined,
    loading: false,
    refresh: async () => {},
  }),
  createPeopleView: async () => ({}),
  updatePeopleView: async () => ({}),
  deletePeopleView: async () => {},
  reorderPeopleViews: async () => ({}),
}));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({ current: [], error: undefined, loading: false, refresh: async () => {} }),
  getPerson: () => {
    throw new Error("No person is open");
  },
  getOverview: (args: PeopleOverviewRequest) => {
    const key = sortKey(args);
    requested.overview = key;
    return {
      get current() {
        return overviews[key];
      },
      error: undefined,
      loading: false,
      refresh: async () => {},
    };
  },
  getReview: (args: PeopleReviewRequest) => {
    const key = sortKey(args);
    requested.review = key;
    return {
      get current() {
        return reviews[key];
      },
      error: undefined,
      loading: false,
      refresh: async () => {},
    };
  },
}));

import PeoplePage from "./PeoplePage.svelte";
import { FIRST_ROWS, ROWS_PER_STEP } from "./PeopleRoster.svelte";

const PEOPLE = 300;
const NO_SORT = JSON.stringify(null);
const FRAME_CAP_MS = 300;

interface Sample {
  lens: string;
  rate: number;
  scenario: string;
  longestFrameMs: number;
  longTaskTotalMs: number;
  /** Rows its first frame mounted for a render or an answer that replaces the rows, cells for a lens switch. */
  firstFrameMounted?: number;
}

const samples: Sample[] = [];

function frame() {
  return new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
}

function rows() {
  return document.querySelectorAll<HTMLAnchorElement>('[data-testid="person-row"]');
}

/**
 * The longest gap between animation frames, and the total of the long tasks, from `action` until `settled` holds
 * and three more frames have drawn.
 */
async function measure(action: () => unknown, settled: () => boolean) {
  const longTasks: number[] = [];
  const observer = new PerformanceObserver((list) => {
    for (const entry of list.getEntries()) longTasks.push(entry.duration);
  });
  observer.observe({ type: "longtask" });
  await frame();
  await frame();

  const gaps: number[] = [];
  let last = performance.now();
  let running = true;
  const onFrame = (at: number) => {
    gaps.push(at - last);
    last = at;
    if (running) requestAnimationFrame(onFrame);
  };
  requestAnimationFrame(onFrame);

  await action();
  await expect.poll(settled, { timeout: 60_000, interval: 16 }).toBe(true);
  for (let i = 0; i < 3; i++) await frame();
  running = false;
  await new Promise((resolve) => setTimeout(resolve, 50));
  observer.disconnect();

  return {
    longestFrameMs: Math.round(Math.max(...gaps)),
    longTaskTotalMs: Math.round(longTasks.reduce((sum, duration) => sum + duration, 0)),
  };
}

async function record(lens: string, rate: number, scenario: string, action: () => unknown, settled: () => boolean) {
  const sample = { lens, rate, scenario, ...(await measure(action, settled)) };
  samples.push(sample);
  if (BUDGET && rate === 1) expect.soft(sample.longestFrameMs, scenario).toBeLessThan(FRAME_CAP_MS);
}

async function scrollThrough() {
  const scroller = document.scrollingElement ?? document.documentElement;
  for (let top = 0; top < scroller.scrollHeight; top += window.innerHeight / 2) {
    window.scrollTo(0, top);
    await frame();
  }
  window.scrollTo(0, 0);
}

function slugsShown() {
  return [...rows()].map((row) => row.dataset.person);
}

/** In the API's order for a sort: the flagged first, each part by `key`. */
function flaggedFirstBy(people: TenantOverviewItem[], key: (person: TenantOverviewItem) => number) {
  const flagged = people.filter((person) => (person.attention?.length ?? 0) > 0);
  const others = people.filter((person) => (person.attention?.length ?? 0) === 0);
  const byKey = (a: TenantOverviewItem, b: TenantOverviewItem) => key(a) - key(b);
  return [...flagged.sort(byKey), ...others.sort(byKey)];
}

/** A click dispatched in the page, so the measured frames hold no round trip to the test runner. */
function click(element: Element) {
  element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
}

beforeEach(async () => {
  await page.viewport(1400, 900);
  pageState.data = { baseDomain: "example.com" };
  pageState.url = new URL("http://localhost/");
  nav.address = new URL(pageState.url.href);
  overviews = {};
  reviews = {};
});

afterEach(async () => {
  await cdp().send("Emulation.setCPUThrottlingRate", { rate: 1 });
});

/** Where each run writes what it measured, beside the app's other test output. */
const RESULTS = "test-results/people-scale.json";

afterAll(async () => {
  await commands.writeFile(RESULTS, JSON.stringify(samples, null, 2));
});

/**
 * The rates the roster is measured at. The 4x run takes minutes, so it runs only when asked for:
 * `VITE_PEOPLE_SCALE_4X=1 pnpm exec vitest --config vitest.browser.config.ts --run src/lib/components/people/PeopleScale.svelte.test.ts`.
 * `VITE_PEOPLE_SCALE_BUDGET=1` makes a longest frame of {@link FRAME_CAP_MS} or more at 1x fail its scenario.
 */
const RATES = import.meta.env.VITE_PEOPLE_SCALE_4X === "1" ? [1, 4] : [1];
const BUDGET = import.meta.env.VITE_PEOPLE_SCALE_BUDGET === "1";

/** Types `text` into the search box as a person would, and leaves the debounce to send it. */
function typeSearch(text: string) {
  const box = page.getByTestId("people-search").element() as HTMLInputElement;
  box.value = text;
  box.dispatchEvent(new Event("input", { bubbles: true }));
}

/**
 * How many rows, or the `count` of other things, are mounted in the first frame that shows `changed`. The check runs
 * first in each frame, so it sees what the answer's own render mounted, before any reveal step of that frame. Unlike
 * the frame times, this does not depend on how fast the machine is.
 */
function mountedInFirstFrameWhere(changed: () => boolean, count = () => rows().length) {
  return new Promise<number>((resolve) => {
    const onFrame = () => (changed() ? resolve(count()) : requestAnimationFrame(onFrame));
    requestAnimationFrame(onFrame);
  });
}

/** Fails when the first frame that showed the answer mounted more than a reveal step past the first rows. */
async function expectMountedAStepAtATime(firstFrame: Promise<number>) {
  const mounted = await firstFrame;
  const sample = samples[samples.length - 1];
  sample.firstFrameMounted = mounted;
  expect.soft(mounted, `${sample.scenario}: mounted in the first frame`).toBeLessThanOrEqual(FIRST_ROWS + ROWS_PER_STEP);
}

const ALL_CHANGES: MutationObserverInit = { subtree: true, childList: true, attributes: true, characterData: true };

/** What a keyed move leaves alone: it is a remove and an insert of the same row, so `childList` stays out. */
const CHANGES_IN_PLACE: MutationObserverInit = { subtree: true, characterData: true, attributes: true };

/** Watches `people-rows` for the changes in `init`. */
function watchRoster(init: MutationObserverInit) {
  const records: MutationRecord[] = [];
  const observer = new MutationObserver((batch) => records.push(...batch));
  observer.observe(page.getByTestId("people-rows").element(), init);
  const watcher = {
    /** What changed since the last call. */
    drain() {
      records.push(...observer.takeRecords());
      return records.splice(0);
    },
    /** What changed since the last call, and no more after it. */
    stop() {
      const rest = watcher.drain();
      observer.disconnect();
      return rest;
    },
  };
  return watcher;
}

/**
 * Resolves when the roster has changed in none of three frames in a row, so no reveal step is left to run ahead of
 * the next first-frame check.
 */
async function rosterQuiet() {
  const watcher = watchRoster(ALL_CHANGES);
  const deadline = performance.now() + 30_000;
  try {
    for (let quiet = 0; quiet < 3; ) {
      if (performance.now() > deadline) throw new Error("The roster kept changing for 30 seconds");
      await frame();
      quiet = watcher.drain().length === 0 ? quiet + 1 : 0;
    }
  } finally {
    watcher.stop();
  }
}

/** The people whose rows `mutations` touched: inside a row, or a row added or removed. */
function touchedRows(mutations: MutationRecord[]) {
  const touched = new Set<string>();
  const rowOf = (node: Node) =>
    (node instanceof Element ? node : node.parentElement)?.closest<HTMLElement>("[data-person]");
  const note = (row: HTMLElement | null | undefined) => {
    if (row?.dataset.person) touched.add(row.dataset.person);
  };
  for (const mutation of mutations) {
    note(rowOf(mutation.target));
    for (const node of [...mutation.addedNodes, ...mutation.removedNodes]) {
      note(rowOf(node));
      if (node instanceof Element) node.querySelectorAll<HTMLElement>("[data-person]").forEach(note);
    }
  }
  return touched;
}

/** A row's link carries the address, which holds the sort, so a sort moves the `href` of every row and nothing else. */
function withoutLinkMoves(mutations: MutationRecord[]) {
  return mutations.filter(
    (mutation) =>
      !(
        mutation.attributeName === "href" &&
        mutation.target instanceof Element &&
        mutation.target.matches('[data-testid="person-row"]')
      )
  );
}

/** Whether every row's link carries the sort the address holds now. */
function rowLinksCarryTheSort() {
  const sort = nav.address.searchParams.get("sort");
  return sort !== null && [...rows()].every((row) => new URL(row.href, nav.address).searchParams.get("sort") === sort);
}

/** How many mounted rows are not elements of `before`: a sort moves the rows it has and mounts none again. */
function rowsMountedSince(before: Set<Element>) {
  return [...rows()].filter((row) => !before.has(row)).length;
}

const mountedWithTestId = (testId: string) => () => document.querySelectorAll(`[data-testid="${testId}"]`).length;
const reviewGmis = mountedWithTestId("review-gmi");
const reviewLoadings = mountedWithTestId("review-loading");
const pumpCells = mountedWithTestId("pump-cell");

describe.each(RATES)("a 300-person roster at %ix CPU", (rate) => {
  it("renders, scrolls, takes a poll, sorts and switches lens in the Now lens", { timeout: 600_000 }, async () => {
    const people = crowd(PEOPLE);
    overviews = { [NO_SORT]: answerOf(people) };
    await cdp().send("Emulation.setCPUThrottlingRate", { rate });

    const firstRender = mountedInFirstFrameWhere(() => rows().length > 0);
    await record("now", rate, "first render", () => render(PeoplePage), () => rows().length === PEOPLE);
    await expectMountedAStepAtATime(firstRender);
    await record("now", rate, "scroll", scrollThrough, () => true);

    const changed = people.filter((person) => !person.attention?.length).slice(0, 10);
    const newlyFlagged = people.filter((person) => !person.attention?.length).at(-1)!;
    const polled = nextPoll(people, changed.map((person) => person.slug!), newlyFlagged.slug!);
    await record(
      "now",
      rate,
      "live update",
      () => {
        overviews = { ...overviews, [NO_SORT]: answerOf(polled) };
        poll.tick();
      },
      () => slugsShown().indexOf(newlyFlagged.slug) < PEOPLE / 3 + 1
    );

    const sorted = flaggedFirstBy(polled, (person) => person.latest?.mgdl ?? 0);
    const rowsBeforeSort = new Set(rows());
    const sortWatcher = watchRoster(CHANGES_IN_PLACE);
    await record(
      "now",
      rate,
      "sort",
      async () => {
        click(page.getByRole("button", { name: /^Glucose/ }).element());
        await frame();
        overviews = { ...overviews, [requested.overview]: answerOf(sorted) };
      },
      () => slugsShown()[0] === sorted[0].slug
    );
    expect.soft(rowsMountedSince(rowsBeforeSort), "rows mounted again by the sort").toBe(0);
    await rosterQuiet();
    expect.soft([...touchedRows(withoutLinkMoves(sortWatcher.stop()))], "rows the sort changed beyond their link").toEqual([]);
    expect.soft(rowLinksCarryTheSort(), "every row's link carries the new sort").toBe(true);

    reviews = {};
    await rosterQuiet();
    const reviewRowsMounted = mountedInFirstFrameWhere(() => reviewLoadings() > 0);
    await record(
      "now",
      rate,
      "lens switch",
      async () => {
        click(page.getByRole("radio", { name: "Review" }).element());
        await frame();
        reviews = { [requested.review]: crowdReview(sorted) };
      },
      () => reviewGmis() === PEOPLE
    );
    await expectMountedAStepAtATime(reviewRowsMounted);
  });

  it("renders, scrolls, takes a poll, sorts and switches lens in the Review lens", { timeout: 600_000 }, async () => {
    const people = crowd(PEOPLE);
    overviews = { [NO_SORT]: answerOf(people) };
    reviews = { [NO_SORT]: crowdReview(people) };
    pageState.url = new URL("http://localhost/?lens=review&days=14");
    nav.address = new URL(pageState.url.href);
    await cdp().send("Emulation.setCPUThrottlingRate", { rate });

    const firstRender = mountedInFirstFrameWhere(() => rows().length > 0);
    await record("review", rate, "first render", () => render(PeoplePage), () => reviewGmis() === PEOPLE);
    await expectMountedAStepAtATime(firstRender);
    await record("review", rate, "scroll", scrollThrough, () => true);

    const changed = people.filter((person) => !person.attention?.length).slice(0, 10);
    const newlyFlagged = people.filter((person) => !person.attention?.length).at(-1)!;
    const polled = nextPoll(people, changed.map((person) => person.slug!), newlyFlagged.slug!);
    await record(
      "review",
      rate,
      "live update",
      () => {
        overviews = { ...overviews, [NO_SORT]: answerOf(polled) };
        poll.tick();
      },
      () => slugsShown().indexOf(newlyFlagged.slug) < PEOPLE / 3 + 1
    );

    const byTimeInRange = flaggedFirstBy(polled, (person) => crowdIndex(person) % 50);
    const rowsBeforeSort = new Set(rows());
    const sortWatcher = watchRoster(CHANGES_IN_PLACE);
    await record(
      "review",
      rate,
      "sort",
      async () => {
        click(page.getByRole("button", { name: "Time in range" }).element());
        await frame();
        overviews = { ...overviews, [requested.overview]: answerOf(polled) };
        reviews = { ...reviews, [requested.review]: crowdReview(byTimeInRange) };
      },
      () => slugsShown()[0] === byTimeInRange[0].slug
    );
    expect.soft(rowsMountedSince(rowsBeforeSort), "rows mounted again by the sort").toBe(0);
    await rosterQuiet();
    expect.soft([...touchedRows(withoutLinkMoves(sortWatcher.stop()))], "rows the sort changed beyond their link").toEqual([]);
    expect.soft(rowLinksCarryTheSort(), "every row's link carries the new sort").toBe(true);

    const pumpCellsMounted = mountedInFirstFrameWhere(() => pumpCells() > 0, pumpCells);
    await record(
      "review",
      rate,
      "lens switch",
      () => click(page.getByRole("radio", { name: "Now" }).element()),
      () => pumpCells() === PEOPLE
    );
    await expectMountedAStepAtATime(pumpCellsMounted);
  });

  it("mounts the people a cleared search brings back a step at a time", { timeout: 600_000 }, async () => {
    const people = crowd(PEOPLE);
    const found = people[PEOPLE - 1];
    overviews = {
      [NO_SORT]: answerOf(people),
      [JSON.stringify([null, found.slug])]: answerOf([found]),
    };
    render(PeoplePage);
    await expect.poll(() => rows().length, { timeout: 60_000 }).toBe(PEOPLE);
    typeSearch(found.slug!);
    await expect.poll(() => slugsShown(), { timeout: 30_000 }).toEqual([found.slug]);
    await cdp().send("Emulation.setCPUThrottlingRate", { rate });

    const firstFrame = mountedInFirstFrameWhere(() => rows().length !== 1);
    await record("now", rate, "search cleared", () => typeSearch(""), () => rows().length === PEOPLE);
    await expectMountedAStepAtATime(firstFrame);
  });

  it("mounts the people of a second search the same size as the first a step at a time", { timeout: 600_000 }, async () => {
    const people = crowd(PEOPLE);
    const before = people.slice(0, PEOPLE / 2);
    const after = people.slice(PEOPLE / 2);
    overviews = {
      [NO_SORT]: answerOf(before),
      [JSON.stringify([null, "other"])]: answerOf(after),
    };
    render(PeoplePage);
    await expect.poll(() => rows().length, { timeout: 60_000 }).toBe(before.length);
    await cdp().send("Emulation.setCPUThrottlingRate", { rate });

    const afterSlugs = after.map((person) => person.slug);
    const firstFrame = mountedInFirstFrameWhere(() => slugsShown()[0] !== before[0].slug);
    await record(
      "now",
      rate,
      "same-size swap",
      () => typeSearch("other"),
      () => slugsShown().join() === afterSlugs.join()
    );
    await expectMountedAStepAtATime(firstFrame);
  });
});

describe("a poll answer for a 300-person roster", () => {
  it("changes only the rows of the people whose data changed", { timeout: 120_000 }, async () => {
    const people = crowd(PEOPLE);
    overviews = { [NO_SORT]: answerOf(people) };
    render(PeoplePage);
    await expect.poll(() => rows().length, { timeout: 60_000 }).toBe(PEOPLE);
    await frame();

    const unflagged = people.filter((person) => !person.attention?.length);
    const changed = unflagged.slice(0, 10).map((person) => person.slug!);
    const flagged = unflagged.at(-1)!.slug!;
    const watcher = watchRoster(ALL_CHANGES);

    overviews = { ...overviews, [NO_SORT]: answerOf(nextPoll(people, changed, flagged)) };
    poll.tick();
    await expect.poll(() => slugsShown().indexOf(flagged), { timeout: 30_000 }).toBeLessThan(PEOPLE / 3 + 1);
    await frame();
    await frame();

    expect([...touchedRows(watcher.stop())].sort()).toEqual([...changed, flagged].sort());
  });
});
