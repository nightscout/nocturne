import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import {
  GlucoseStatus,
  NotificationUrgency,
  OverviewRuleSet,
  PeopleFilterKind,
  PeopleLens,
  PeopleSortColumn,
  PeopleSortDirection,
  TenantOverviewAttentionKind,
  type PeopleViewSummary,
  type StaffGroup,
  type TenantOverviewItem,
  type TenantReviewResponse,
} from "@nocturne/api-client";
import {
  PeopleOverviewRequestSchema,
  PeopleReviewRequestSchema,
  PeopleViewOrderRequestSchema,
  PeopleViewRequestSchema,
} from "@nocturne/api-client/schemas";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { makePerson, makeReview, makeReviewEntry } from "./people-fixtures.js";
import { RULES_PENDING_REREAD_MS } from "./people-overview.svelte.js";

const BIRCH = "0193b6a0-0000-7000-8000-000000000001";
const TEAM = "0193b6a0-0000-7000-8000-0000000000e1";
const LOWS = "0193b6a0-0000-7000-8000-0000000000a1";
const FORTNIGHT = "0193b6a0-0000-7000-8000-0000000000a2";
const STALE = "0193b6a0-0000-7000-8000-0000000000a3";
const CREATED = "0193b6a0-0000-7000-8000-0000000000a4";

const birch: StaffGroup = { id: BIRCH, name: "Birch cabin", teamName: "Camp Wren", sharesName: false };
const MEAN_FILTER = "fact.glucose_statistics.mean.14.above.180";

const lows: PeopleViewSummary = {
  view: {
    id: LOWS,
    name: "Lows",
    position: 0,
    query: {
      filters: [{ kind: PeopleFilterKind.Flagged }],
      sort: { column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Descending },
    },
    lens: PeopleLens.Now,
  },
  groupGone: false,
  teamGone: false,
};
const fortnight: PeopleViewSummary = {
  view: { id: FORTNIGHT, name: "Fortnight", position: 1, query: { search: "a" }, lens: PeopleLens.Review, reviewDays: 30 },
  groupGone: false,
  teamGone: false,
};
const stale: PeopleViewSummary = {
  view: {
    id: STALE,
    name: "Old cabin",
    position: 2,
    query: { filters: [{ kind: PeopleFilterKind.InGroup, groupId: "0193b6a0-0000-7000-8000-0000000000ff" }] },
    lens: PeopleLens.Now,
  },
  groupGone: true,
  teamGone: false,
};

const state = $state<{
  groups: StaffGroup[] | undefined;
  views: PeopleViewSummary[] | undefined;
  viewsError: unknown;
  limit: number;
  tenants: TenantOverviewItem[];
  pending: number;
  review: TenantReviewResponse | undefined;
  /** A read this says has not answered yet has no value, as a new remote query has none until it answers. */
  unanswered: (args: Record<string, unknown>) => boolean;
  facts: "ready" | "loading" | "failed";
}>({
  groups: [],
  views: [],
  viewsError: undefined,
  limit: 20,
  tenants: [],
  pending: 0,
  review: undefined,
  unanswered: () => false,
  facts: "ready",
});

/** What the API answers to the next read of the views. */
const server = { views: [] as PeopleViewSummary[] };

const calls = vi.hoisted(() => ({
  overview: [] as Record<string, unknown>[],
  review: [] as Record<string, unknown>[],
  groupReads: 0,
  viewReads: 0,
  reviewRefreshes: 0,
  create: vi.fn(async (_request: unknown): Promise<unknown> => ({})),
  update: vi.fn(async (_args: unknown): Promise<unknown> => ({})),
  remove: vi.fn(async (_viewId: unknown): Promise<unknown> => ({})),
  reorder: vi.fn(async (_request: unknown): Promise<unknown> => ({})),
}));
const nav = vi.hoisted(() => ({
  afterNavigate: () => {},
  address: new URL("http://localhost/"),
}));

vi.mock("$app/navigation", () => ({
  replaceState: (url: string | URL) => {
    nav.address = new URL(url, nav.address.href);
  },
  afterNavigate: (callback: () => void) => {
    nav.afterNavigate = callback;
  },
}));

vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({ pollWhileVisible: () => {} }));
vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({
    get current() {
      return state.groups;
    },
    error: undefined,
    loading: false,
    refresh: async () => {
      calls.groupReads++;
    },
  }),
  getOverview: (args: Record<string, unknown>) => {
    calls.overview.push(args);
    return {
      get current() {
        return state.unanswered(args) ? undefined : { tenants: state.tenants, peopleWithRulesPending: state.pending };
      },
      error: undefined,
      get loading() {
        return state.unanswered(args);
      },
      refresh: async () => {},
    };
  },
  getReview: (args: Record<string, unknown>) => {
    calls.review.push(args);
    return {
      get current() {
        return state.unanswered(args) ? undefined : state.review;
      },
      error: undefined,
      get loading() {
        return state.unanswered(args);
      },
      refresh: async () => {
        calls.reviewRefreshes++;
      },
    };
  },
  getPerson: () => ({ current: undefined, error: undefined, loading: true, refresh: async () => {} }),
}));

vi.mock("#lib/api/generated/peopleViews.generated.remote.js", () => ({
  getPeopleViews: () => ({
    get current() {
      return state.views ? { views: state.views, limit: state.limit } : undefined;
    },
    get error() {
      return state.viewsError;
    },
    loading: false,
    refresh: async () => {
      calls.viewReads++;
      await Promise.resolve();
      state.views = server.views;
    },
  }),
  createPeopleView: (request: unknown) => calls.create(request),
  updatePeopleView: (args: unknown) => calls.update(args),
  deletePeopleView: (viewId: unknown) => calls.remove(viewId),
  reorderPeopleViews: (request: unknown) => calls.reorder(request),
}));

vi.mock("#lib/api/generated/flagRules.generated.remote.js", async () => {
  const fixtures = await import("./flag-rule-fixtures.js");
  return {
    getFlagRuleFacts: () => ({
      get current() {
        return state.facts === "ready" ? { presets: fixtures.FLAG_PRESETS, facts: fixtures.FLAG_FACTS } : undefined;
      },
      get error() {
        return state.facts === "failed" ? new Error("down") : undefined;
      },
      refresh: async () => {
        state.facts = "ready";
      },
    }),
  };
});

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getMyTeams: () => ({
    current: { items: [{ id: "0193b6a0-0000-7000-8000-0000000000e1", name: "Ward 4", kind: "Staff" }], canCreate: false },
    error: undefined,
    refresh: async () => {},
  }),
}));

import PeoplePage from "./PeoplePage.svelte";

function flagged(slug: string): TenantOverviewItem {
  return makePerson({
    slug,
    status: GlucoseStatus.Low,
    attention: [{ kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent }],
  });
}

const PEOPLE = ["amy", "bo", "cy", "di", "ed", "fay"];

function openAt(search: string) {
  pageState.url = new URL(`http://localhost/people${search}`);
  nav.address = new URL(pageState.url.href);
  return render(PeoplePage);
}

const tab = (name: string | RegExp) => page.getByRole("tab", { name });
const lastOverview = () => calls.overview.at(-1);
const lastReview = () => calls.review.at(-1);

function names() {
  return page
    .getByTestId("person-row")
    .elements()
    .map((row) => row.querySelector("p")?.textContent?.trim());
}

async function chooseFilter(label: string) {
  await page.getByRole("button", { name: "Add a filter" }).click();
  await page.getByTestId("people-filter-kind").click();
  await page.getByRole("option", { name: label, exact: true }).click();
}

function rejection(status: number, code?: string) {
  try {
    error(status, "Request rejected", code ? { code } : {});
  } catch (thrown) {
    return thrown;
  }
}

beforeEach(async () => {
  await page.viewport(1100, 900);
  pageState.data = { baseDomain: "example.com" };
  pageState.shallow = null;
  state.groups = [birch];
  state.views = [lows, fortnight];
  server.views = [lows, fortnight];
  state.viewsError = undefined;
  state.limit = 20;
  state.tenants = PEOPLE.map((slug) => makePerson({ slug }));
  state.pending = 0;
  state.review = undefined;
  state.unanswered = () => false;
  state.facts = "ready";
  calls.overview.length = 0;
  calls.review.length = 0;
  calls.groupReads = 0;
  calls.viewReads = 0;
  calls.reviewRefreshes = 0;
  for (const command of [calls.create, calls.update, calls.remove, calls.reorder]) {
    command.mockReset();
    command.mockImplementation(async () => ({}));
  }
  glucoseUnits.current = "mg/dl";
});

afterEach(() => {
  vi.useRealTimers();
  glucoseUnits.current = "mg/dl";
});

describe("the tab strip", () => {
  it("lists Everyone, the groups, a rule that is not a tab, then the views in their order", async () => {
    openAt("");

    await expect.element(page.getByRole("tablist", { name: "Lists of people" })).toBeVisible();
    expect(page.getByRole("tab").elements().map((t) => t.textContent?.trim())).toEqual([
      "Everyone",
      "Birch cabin",
      "Lows",
      "Fortnight",
    ]);
    const divider = page.getByTestId("people-tabs-divider").element();
    expect(divider.getAttribute("aria-hidden")).toBe("true");
    expect(divider.getAttribute("role")).toBeNull();
    expect(divider.hasAttribute("tabindex")).toBe(false);
    const [, birchTab, lowsTab] = page.getByRole("tab").elements();
    expect(divider.compareDocumentPosition(birchTab) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy();
    expect(divider.compareDocumentPosition(lowsTab) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("shows the strip for views alone, and no rule without views", async () => {
    state.groups = [];
    openAt("");
    await expect.element(tab("Lows")).toBeVisible();
    expect(page.getByRole("tab").elements().map((t) => t.textContent?.trim())).toEqual(["Everyone", "Lows", "Fortnight"]);
  });

  it("shows no rule when the viewer has groups and no views", async () => {
    state.views = [];
    openAt("");
    await expect.element(tab("Birch cabin")).toBeVisible();
    expect(page.getByTestId("people-tabs-divider").elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Manage views" }).elements()).toHaveLength(0);
  });

  it("says on a view's tab, in words its name holds, that its group is gone, and why it matches no one", async () => {
    state.views = [lows, stale];
    openAt("");

    await expect.element(tab("Old cabin Group gone")).toHaveTextContent(/Old cabin\s*·\s*Group gone/);
    expect(tab("Old cabin Group gone").element().hasAttribute("aria-label")).toBe(false);
    await tab("Old cabin Group gone").click();
    await expect.element(page.getByTestId("people-view-group-gone")).toHaveTextContent(
      'This view has an "In a group" filter for a group you are no longer in. That filter matches no one.'
    );
  });

  it("says, for a view whose team the viewer follows no one through, only that, not that the viewer left it", async () => {
    state.views = [{ ...lows, teamGone: true }];
    openAt(`?view=${LOWS}`);

    await expect.element(tab("Lows No one through team")).toHaveTextContent(/Lows\s*·\s*No one through team/);
    expect(tab("Lows No one through team").element().hasAttribute("aria-label")).toBe(false);
    await expect.element(page.getByTestId("people-view-team-gone")).toHaveTextContent(
      'This view has a "Followed through a team" filter. You follow no one through that team, so that filter matches no one.'
    );
    expect(page.getByText(/no longer in|not yours/).elements()).toHaveLength(0);
  });

  it("says when a view's group is gone and its team follows no one", async () => {
    state.views = [{ ...stale, teamGone: true }];
    openAt("");
    await expect
      .element(tab("Old cabin Group gone, no one through team"))
      .toHaveTextContent(/Old cabin\s*·\s*Group gone, no one through team/);
  });

  it("falls back to everyone for a view the viewer does not have, without the groups", async () => {
    state.groups = undefined;
    openAt("?view=0193b6a0-0000-7000-8000-0000000000ee");

    await expect
      .element(page.getByText("You have no view with that link. The list shows everyone you follow."))
      .toBeVisible();
    expect(nav.address.searchParams.has("view")).toBe(false);
    expect(calls.groupReads).toBe(0);
    expect(calls.overview.every((args) => !("group" in args))).toBe(true);
  });

  it("falls back to everyone when the views do not load", async () => {
    state.views = undefined;
    state.viewsError = new Error("down");
    openAt(`?view=${LOWS}`);

    await expect.element(page.getByText("Your views did not load. The list shows everyone you follow.")).toBeVisible();
  });

  it("reads the views again before it refuses a view a navigation names", async () => {
    openAt("");
    await expect.element(tab("Lows")).toBeVisible();
    const created = { ...lows, view: { ...lows.view, id: CREATED, name: "New one", position: 2 } };
    server.views = [lows, fortnight, created];

    pageState.url = new URL(`http://localhost/people?view=${CREATED}`);
    nav.afterNavigate();

    await expect.element(tab("New one")).toHaveAttribute("aria-selected", "true");
    expect(calls.viewReads).toBe(1);
    expect(page.getByText(/You have no view with that link/).elements()).toHaveLength(0);
  });
});

describe("the reads", () => {
  it("reads Everyone and a group with every rule and no query, and a view with its own query and no group", async () => {
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(lastOverview()).toEqual({ rules: OverviewRuleSet.All });

    await tab("Birch cabin").click();
    await expect.poll(lastOverview).toEqual({ group: BIRCH, rules: OverviewRuleSet.All });

    await tab("Lows").click();
    await expect.poll(lastOverview).toEqual({
      rules: OverviewRuleSet.All,
      query: lows.view.query,
    });
    for (const args of calls.overview) expect(PeopleOverviewRequestSchema.safeParse(args).success).toBe(true);
    expect(nav.address.search).toBe(`?view=${LOWS}`);
  });

  it("reads a view with the address's unsaved changes over its own query", async () => {
    openAt(`?view=${LOWS}&q=bo&sort=none`);
    await expect.element(page.getByText("Amy")).toBeVisible();

    expect(lastOverview()).toEqual({
      rules: OverviewRuleSet.All,
      query: { search: "bo", filters: [{ kind: PeopleFilterKind.Flagged }] },
    });
    await expect.element(page.getByTestId("people-search")).toHaveValue("bo");
  });

  it("opens a Review view in its period and reads the review with every rule and its query", async () => {
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt(`?view=${FORTNIGHT}`);

    await expect.poll(lastReview).toEqual({ rules: OverviewRuleSet.All, days: 30, query: { search: "a" } });
    expect(PeopleReviewRequestSchema.safeParse(lastReview()).success).toBe(true);
    await expect.element(page.getByRole("radio", { name: "30 days" })).toHaveAttribute("aria-checked", "true");
    expect(nav.address.search).toBe(`?view=${FORTNIGHT}`);
  });

  it("reads a group with the filters its address holds", async () => {
    openAt(`?group=${BIRCH}&filters=flagged,age-at-least.18`);
    await expect.element(page.getByText("Amy")).toBeVisible();

    expect(lastOverview()).toEqual({
      group: BIRCH,
      rules: OverviewRuleSet.All,
      query: { filters: [{ kind: PeopleFilterKind.Flagged }, { kind: PeopleFilterKind.AgeAtLeast, years: 18 }] },
    });
  });

  it("says, while people are checked for a data filter, that the list may grow, counted by the lens's own read", async () => {
    state.pending = 3;
    state.review = { ...makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug })))), peopleWithRulesPending: 1 };
    openAt("?filters=fact.glucose_statistics.timeBelow.14.above.4");

    await expect.element(page.getByTestId("people-still-checking")).toHaveTextContent(
      "Still checking 3 people. The list may grow."
    );

    await page.getByRole("radio", { name: "Review" }).click();
    await expect.element(page.getByTestId("people-still-checking")).toHaveTextContent(
      "Still checking 1 person. The list may grow."
    );
  });

  it("reads the review again sooner while the review says people are still being checked", async () => {
    vi.useFakeTimers();
    state.review = { ...makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug })))), peopleWithRulesPending: 2 };
    openAt("?lens=review&days=14");
    flushSync();

    await vi.advanceTimersByTimeAsync(RULES_PENDING_REREAD_MS - 1);
    expect(calls.reviewRefreshes).toBe(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(calls.reviewRefreshes).toBe(1);
  });

  it("does not read the review again sooner once no one is pending", async () => {
    vi.useFakeTimers();
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?lens=review&days=14");
    flushSync();

    await vi.advanceTimersByTimeAsync(RULES_PENDING_REREAD_MS * 4);
    expect(calls.reviewRefreshes).toBe(0);
  });

  it("says nothing of people being checked when the query has no data filter", async () => {
    state.pending = 3;
    openAt("?filters=flagged");
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByTestId("people-still-checking").elements()).toHaveLength(0);
  });

  it("says no one matches when a query leaves no one", async () => {
    state.tenants = [];
    openAt("?q=zed");
    await expect.element(page.getByRole("heading", { name: "No people match" })).toBeVisible();
  });
});

describe("the search", () => {
  it("reads the list for the search once the typing stops, not on every key", async () => {
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();

    await userEvent.type(page.getByTestId("people-search"), "bo");

    await expect.poll(() => (lastOverview()?.query as { search?: string } | undefined)?.search).toBe("bo");
    const searches = calls.overview.flatMap((args) => {
      const search = (args.query as { search?: string } | undefined)?.search;
      return search ? [search] : [];
    });
    expect(searches.every((search) => search === "bo")).toBe(true);
    expect(nav.address.searchParams.get("q")).toBe("bo");
  });
});

describe("the filters", () => {
  it("says filters combine with AND", async () => {
    openAt("");
    await page.getByRole("button", { name: "Add a filter" }).click();
    await expect
      .element(page.getByTestId("people-filter-editor"))
      .toHaveTextContent("Filters combine with AND: the list shows a person only if the person matches all the filters.");
  });

  it("adds an age bound as a chip, reads it and writes it in the address, and removes it", async () => {
    openAt("");
    await chooseFilter("Age at least");
    await page.getByRole("spinbutton", { name: "Age in years" }).fill("18");
    await page.getByRole("button", { name: "Add filter" }).click();

    await expect.element(page.getByTestId("people-filter")).toHaveTextContent("Age is at least 18 years");
    await expect
      .poll(() => (lastOverview()?.query as { filters?: unknown[] } | undefined)?.filters)
      .toEqual([{ kind: PeopleFilterKind.AgeAtLeast, years: 18 }]);
    expect(nav.address.searchParams.get("filters")).toBe("age-at-least.18");

    await page.getByRole("button", { name: "Remove the filter: Age is at least 18 years" }).click();
    await expect.poll(lastOverview).toEqual({ rules: OverviewRuleSet.All });
    expect(nav.address.searchParams.has("filters")).toBe(false);
  });

  it("adds an age upper bound", async () => {
    openAt("");
    await chooseFilter("Age at most");
    await page.getByRole("spinbutton", { name: "Age in years" }).fill("12");
    await page.getByRole("button", { name: "Add filter" }).click();
    await expect.element(page.getByTestId("people-filter")).toHaveTextContent("Age is at most 12 years");
  });

  it("adds therapy types, any of them", async () => {
    openAt("");
    await chooseFilter("Therapy type");
    await expect.element(page.getByRole("button", { name: "Add filter" })).toBeDisabled();
    await page.getByRole("checkbox", { name: "Injections" }).click();
    await page.getByRole("checkbox", { name: "Pump", exact: true }).click();
    await page.getByRole("button", { name: "Add filter" }).click();

    await expect.element(page.getByTestId("people-filter")).toHaveTextContent("Therapy type: Pump or Injections");
    await expect
      .poll(() => (lastOverview()?.query as { filters?: unknown[] } | undefined)?.filters)
      .toEqual([{ kind: PeopleFilterKind.Therapy, therapyTypes: ["Pump", "Injections"] }]);
  });

  it("adds flagged only, followed directly, a team and a group", async () => {
    openAt("");
    await chooseFilter("Flagged only");
    await page.getByRole("button", { name: "Add filter" }).click();
    await chooseFilter("Followed directly");
    await page.getByRole("button", { name: "Add filter" }).click();
    await chooseFilter("Followed through a team");
    await page.getByLabelText("Team", { exact: true }).click();
    await page.getByRole("option", { name: "Ward 4" }).click();
    await page.getByRole("button", { name: "Add filter" }).click();
    await chooseFilter("In a group");
    await page.getByLabelText("Group", { exact: true }).click();
    await page.getByRole("option", { name: "Birch cabin" }).click();
    await page.getByRole("button", { name: "Add filter" }).click();

    await expect.poll(() => page.getByTestId("people-filter").elements().map((chip) => chip.textContent?.trim())).toEqual([
      "Flagged only",
      "Followed directly",
      "Followed through Ward 4",
      "In the group Birch cabin",
    ]);
    expect((lastOverview()?.query as { filters?: unknown[] }).filters).toEqual([
      { kind: PeopleFilterKind.Flagged },
      { kind: PeopleFilterKind.SourceDirect },
      { kind: PeopleFilterKind.SourceTeam, teamId: TEAM },
      { kind: PeopleFilterKind.InGroup, groupId: BIRCH },
    ]);
    expect(nav.address.searchParams.get("filters")).toBe(`flagged,direct,team.${TEAM},group.${BIRCH}`);
    expect(PeopleOverviewRequestSchema.safeParse(lastOverview()).success).toBe(true);
  });

  it("names a group whose name another group shares with its team, on the option and the chip", async () => {
    state.groups = [{ ...birch, sharesName: true }];
    openAt("");
    await chooseFilter("In a group");
    await page.getByLabelText("Group", { exact: true }).click();
    await expect.element(page.getByRole("option", { name: "Birch cabin Camp Wren" })).toBeVisible();
    await page.getByRole("option", { name: "Birch cabin Camp Wren" }).click();
    await page.getByRole("button", { name: "Add filter" }).click();

    await expect.element(page.getByTestId("people-filter")).toHaveTextContent(/In the group Birch cabin\s*·\s*Camp Wren/);
    await expect
      .element(page.getByRole("button", { name: "Remove the filter: In the group Birch cabin, Camp Wren" }))
      .toBeVisible();
  });

  it("states no value on a data filter's chip while the facts load, and offers to read them again when they fail", async () => {
    glucoseUnits.current = "mmol";
    state.facts = "loading";
    openAt(`?filters=${MEAN_FILTER}`);

    await expect.element(page.getByTestId("people-filter")).toHaveTextContent("Loading the data filter…");
    expect(page.getByTestId("people-filter").element().textContent).not.toMatch(/\d/);

    state.facts = "failed";
    await expect.element(page.getByTestId("people-filter")).toHaveTextContent("A filter by a figure of their data");
    expect(page.getByTestId("people-filter").element().textContent).not.toMatch(/\d/);
    await expect
      .element(page.getByTestId("people-filter-facts-failed"))
      .toHaveTextContent("The list of figures did not load. The data filters show no values.");

    await page.getByTestId("people-filter-facts-failed").getByRole("button", { name: "Try again" }).click();
    await expect.element(page.getByTestId("people-filter")).toHaveTextContent(
      "Mean glucose in the last 14 days is above 10 mmol/L"
    );
    expect(page.getByTestId("people-filter-facts-failed").elements()).toHaveLength(0);
  });

  it("adds a data filter from the rule's fact picker, with no level, a glucose value in mmol/L sent in mg/dL", async () => {
    glucoseUnits.current = "mmol";
    openAt("");
    await chooseFilter("A figure of their data");

    await expect.element(page.getByText("Suggested filters")).toBeVisible();
    await page.getByRole("button", { name: "More facts" }).click();
    await page.getByTestId("flag-rule-fact-picker").click();
    await page.getByRole("option", { name: "Mean glucose" }).click();
    expect(page.getByText("Level", { exact: true }).elements()).toHaveLength(0);
    await page.getByRole("spinbutton", { name: "Value" }).fill("10");
    await page.getByRole("button", { name: "Add filter" }).click();

    await expect.element(page.getByTestId("people-filter")).toHaveTextContent(
      "Mean glucose in the last 14 days is above 10 mmol/L"
    );
    const [filter] = (lastOverview()?.query as { filters: Record<string, unknown>[] }).filters;
    expect(filter.kind).toBe(PeopleFilterKind.Fact);
    expect(filter.field).toBe("mean");
    expect(filter.days).toBe(14);
    expect(filter.value).toBeCloseTo(180, 0);
    expect(filter).not.toHaveProperty("level");
    expect(PeopleOverviewRequestSchema.safeParse(lastOverview()).success).toBe(true);
  });

  it("takes no more filters than the API allows, and says why", async () => {
    openAt(`?filters=${Array(10).fill("flagged").join(",")}`);

    await expect.element(page.getByRole("button", { name: "Add a filter" })).toBeDisabled();
    await expect.element(page.getByTestId("people-filters-limit")).toHaveTextContent(
      "You have 10 filters, the maximum. Remove a filter to add a new filter."
    );
  });
});

describe("the sort", () => {
  it("sorts by a heading, then reverses, then clears, with aria-sort, and keeps the API's order", async () => {
    state.tenants = [flagged("fay"), ...["ed", "amy", "di", "bo"].map((slug) => makePerson({ slug }))];
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();
    const glucose = page.getByRole("button", { name: "Glucose, mg/dL" });
    const header = () => glucose.element().closest("[role='columnheader']");

    await glucose.click();
    await expect.poll(() => header()?.getAttribute("aria-sort")).toBe("ascending");
    expect((lastOverview()?.query as { sort?: unknown }).sort).toEqual({
      column: PeopleSortColumn.Glucose,
      direction: PeopleSortDirection.Ascending,
    });
    expect(nav.address.searchParams.get("sort")).toBe("glucose.asc");
    expect(names()).toEqual(["Fay", "Ed", "Amy", "Di", "Bo"]);

    await glucose.click();
    await expect.poll(() => header()?.getAttribute("aria-sort")).toBe("descending");
    expect(nav.address.searchParams.get("sort")).toBe("glucose.desc");

    await glucose.click();
    await expect.poll(() => header()?.hasAttribute("aria-sort")).toBe(false);
    expect(lastOverview()).toEqual({ rules: OverviewRuleSet.All });
    expect(nav.address.searchParams.has("sort")).toBe(false);
  });

  it("keeps the list and the header's focus while each sort is read, and says the kept list is being updated", async () => {
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();
    await expect.element(page.getByText("6 people.")).toBeVisible();
    const glucose = page.getByRole("button", { name: "Glucose, mg/dL" });
    const header = () => glucose.element().closest("[role='columnheader']");
    const list = () => page.getByTestId("people-list").element();
    const rows = () => page.getByTestId("people-rows").element();

    async function expectKept(order: string[]) {
      await expect.poll(() => list().getAttribute("aria-busy")).toBe("true");
      await expect.element(page.getByTestId("people-updating")).toHaveTextContent("Updating the list…");
      expect(page.getByText(/^\d+ (people|person)\.$/).elements()).toHaveLength(0);
      expect(rows().classList.contains("opacity-60")).toBe(true);
      expect(names()).toEqual(order);
      expect(page.getByText("Loading…").elements()).toHaveLength(0);
      expect(document.activeElement).toBe(glucose.element());
    }

    async function expectFresh(order: string[]) {
      await expect.poll(() => list().hasAttribute("aria-busy")).toBe(false);
      await expect.poll(names).toEqual(order);
      await expect.element(page.getByText(`${order.length} people.`)).toBeVisible();
      expect(page.getByTestId("people-updating").elements()).toHaveLength(0);
      expect(rows().classList.contains("opacity-60")).toBe(false);
      expect(document.activeElement).toBe(glucose.element());
    }

    state.unanswered = (args) => "query" in args;
    await glucose.click();
    await expect.poll(() => header()?.getAttribute("aria-sort")).toBe("ascending");
    await expectKept(["Amy", "Bo", "Cy", "Di", "Ed", "Fay"]);

    state.tenants = ["fay", "ed", "di", "cy", "bo"].map((slug) => makePerson({ slug }));
    state.unanswered = () => false;
    await expectFresh(["Fay", "Ed", "Di", "Cy", "Bo"]);
    expect(header()?.getAttribute("aria-sort")).toBe("ascending");

    state.unanswered = (args) => "query" in args;
    await glucose.click();
    await expect.poll(() => header()?.getAttribute("aria-sort")).toBe("descending");
    await expectKept(["Fay", "Ed", "Di", "Cy", "Bo"]);

    state.tenants = ["bo", "cy", "di", "ed", "fay"].map((slug) => makePerson({ slug }));
    state.unanswered = () => false;
    await expectFresh(["Bo", "Cy", "Di", "Ed", "Fay"]);
    expect(header()?.getAttribute("aria-sort")).toBe("descending");

    await glucose.click();
    await expect.poll(() => header()?.hasAttribute("aria-sort")).toBe(false);
    await expectFresh(["Bo", "Cy", "Di", "Ed", "Fay"]);
  });

  it("shows no pending line and no empty state of an earlier query while a data filter's new read runs", async () => {
    state.pending = 3;
    state.tenants = [];
    openAt("?filters=fact.glucose_statistics.timeBelow.14.above.4");
    await expect.element(page.getByRole("heading", { name: "No people match" })).toBeVisible();
    await expect.element(page.getByTestId("people-still-checking")).toHaveTextContent(
      "Still checking 3 people. The list may grow."
    );

    state.unanswered = (args) => (args.query as { search?: string } | undefined)?.search === "a";
    await userEvent.type(page.getByTestId("people-search"), "a");

    await expect.poll(() => page.getByTestId("people-list").element().getAttribute("aria-busy")).toBe("true");
    await expect.element(page.getByTestId("people-updating")).toHaveTextContent("Updating the list…");
    expect(page.getByTestId("people-still-checking").elements()).toHaveLength(0);
    expect(page.getByRole("heading", { name: "No people match" }).elements()).toHaveLength(0);
    expect(page.getByText(/^\d+ (people|person)\.$/).elements()).toHaveLength(0);

    state.unanswered = () => false;
    await expect.element(page.getByRole("heading", { name: "No people match" })).toBeVisible();
    await expect.element(page.getByTestId("people-still-checking")).toHaveTextContent(
      "Still checking 3 people. The list may grow."
    );
    expect(page.getByTestId("people-list").element().hasAttribute("aria-busy")).toBe(false);
  });

  it("keeps the rows' order under a review sort until the review of the new query answers", async () => {
    state.review = makeReview(["cy", "amy", "fay", "bo", "ed", "di"].map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?lens=review&days=14&sort=time-in-range.desc");
    await expect.poll(names).toEqual(["Cy", "Amy", "Fay", "Bo", "Ed", "Di"]);

    state.unanswered = (args) => (args.query as { search?: string } | undefined)?.search === "a" && "days" in args;
    await userEvent.type(page.getByTestId("people-search"), "a");

    await expect.poll(() => page.getByTestId("people-list").element().getAttribute("aria-busy")).toBe("true");
    expect(names()).toEqual(["Cy", "Amy", "Fay", "Bo", "Ed", "Di"]);

    state.review = makeReview(["di", "ed", "bo", "fay", "amy", "cy"].map((slug) => makeReviewEntry(makePerson({ slug }))));
    state.unanswered = () => false;
    await expect.poll(names).toEqual(["Di", "Ed", "Bo", "Fay", "Amy", "Cy"]);
    expect(page.getByTestId("people-list").element().hasAttribute("aria-busy")).toBe(false);
  });

  it("says Loading only for the first read", async () => {
    state.unanswered = () => true;
    openAt("");
    await expect.element(page.getByText("Loading…")).toBeVisible();
    expect(page.getByTestId("person-row").elements()).toHaveLength(0);
  });

  it("shows no sort the Review lens does not show, from an address or a Now view opened in Review", async () => {
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?lens=review&days=14&sort=glucose.asc");

    await expect.poll(lastReview).toEqual({ rules: OverviewRuleSet.All, days: 14 });
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(document.querySelectorAll("[aria-sort]")).toHaveLength(0);
  });

  it("saves a Now view opened in Review without the sort Review does not show", async () => {
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt(`?view=${LOWS}&lens=review`);

    await expect.poll(lastReview).toEqual({
      rules: OverviewRuleSet.All,
      days: 14,
      query: { filters: [{ kind: PeopleFilterKind.Flagged }] },
    });
    await page.getByRole("button", { name: "Save view", exact: true }).click();

    await expect.poll(() => calls.update.mock.calls.length).toBe(1);
    const { request } = calls.update.mock.calls[0][0] as { request: unknown };
    expect(request).toEqual({
      name: "Lows",
      query: { filters: [{ kind: PeopleFilterKind.Flagged }] },
      lens: PeopleLens.Review,
      reviewDays: 14,
    });
    expect(PeopleViewRequestSchema.safeParse(request).success).toBe(true);
  });

  it("lists the Review lens in the review's order under a sort by a review figure", async () => {
    state.tenants = PEOPLE.map((slug) => makePerson({ slug }));
    state.review = makeReview(["cy", "amy", "fay", "bo", "ed", "di"].map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?lens=review&days=14&sort=time-in-range.desc");

    await expect.poll(names).toEqual(["Cy", "Amy", "Fay", "Bo", "Ed", "Di"]);
    expect((lastReview()?.query as { sort?: unknown }).sort).toEqual({
      column: PeopleSortColumn.TimeInRange,
      direction: PeopleSortDirection.Descending,
    });
  });

  it("keeps the list's order in the Review lens with no review sort", async () => {
    state.review = makeReview(["cy", "amy", "fay", "bo", "ed", "di"].map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?lens=review&days=14");

    await expect.poll(names).toEqual(["Amy", "Bo", "Cy", "Di", "Ed", "Fay"]);
    expect(lastReview()).toEqual({ rules: OverviewRuleSet.All, days: 14 });
  });

  it("drops a sort the Review lens does not show when the lens changes", async () => {
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt("?sort=glucose.asc");
    await expect.element(page.getByText("Amy")).toBeVisible();

    await page.getByRole("radio", { name: "Review" }).click();

    await expect.poll(lastReview).toEqual({ rules: OverviewRuleSet.All, days: 14 });
    expect(nav.address.searchParams.has("sort")).toBe(false);
  });
});

describe("saving", () => {
  it("offers Save as new view only once there is something to keep", async () => {
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByRole("button", { name: "Save as new view" }).elements()).toHaveLength(0);

    await chooseFilter("Flagged only");
    await page.getByRole("button", { name: "Add filter" }).click();
    await expect.element(page.getByRole("button", { name: "Save as new view" })).toBeVisible();
  });

  it("saves the search, filters, sort and lens as a new view and opens it, its address holding only the view", async () => {
    calls.create.mockImplementation(async () => {
      const created = { ...lows, view: { ...lows.view, id: CREATED, name: "Ward lows", position: 2 } };
      state.views = [lows, fortnight, created];
      return created.view;
    });
    openAt(`?filters=flagged&q=a&sort=glucose.desc`);
    await page.getByRole("button", { name: "Save as new view" }).click();
    await page.getByRole("textbox", { name: "Name" }).fill(" Ward lows ");
    await page.getByRole("dialog").getByRole("button", { name: "Save as new view" }).click();

    await expect.element(tab("Ward lows")).toHaveAttribute("aria-selected", "true");
    const request = calls.create.mock.calls[0][0];
    expect(request).toEqual({
      name: "Ward lows",
      query: {
        search: "a",
        filters: [{ kind: PeopleFilterKind.Flagged }],
        sort: { column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Descending },
      },
      lens: PeopleLens.Now,
    });
    expect(PeopleViewRequestSchema.safeParse(request).success).toBe(true);
    expect(nav.address.search).toBe(`?view=${CREATED}`);
  });

  it("says, saving from a group, that a view starts from everyone", async () => {
    openAt(`?group=${BIRCH}&filters=flagged`);
    await page.getByRole("button", { name: "Save as new view" }).click();
    await expect.element(page.getByTestId("people-view-from-group")).toHaveTextContent(
      'A view starts from everyone you follow, not only this group. To keep to this group, add the filter "In a group".'
    );
  });

  it("shows the API's reason in place when the name is taken", async () => {
    calls.create.mockRejectedValue(rejection(409, "people_view_name_taken"));
    openAt("?filters=flagged");
    await page.getByRole("button", { name: "Save as new view" }).click();
    await page.getByRole("textbox", { name: "Name" }).fill("Lows");
    await page.getByRole("dialog").getByRole("button", { name: "Save as new view" }).click();

    await expect.element(page.getByRole("dialog").getByRole("alert")).toHaveTextContent(
      "You have a view with this name. Use a different name."
    );
    await expect.element(page.getByRole("textbox", { name: "Name" })).toHaveAttribute("aria-invalid", "true");
  });

  it("never calls a failure of the server a taken name", async () => {
    calls.create.mockRejectedValue(rejection(500));
    openAt("?filters=flagged");
    await page.getByRole("button", { name: "Save as new view" }).click();
    await page.getByRole("textbox", { name: "Name" }).fill("Lows");
    await page.getByRole("dialog").getByRole("button", { name: "Save as new view" }).click();

    await expect.element(page.getByRole("dialog").getByRole("alert")).toHaveTextContent("Could not save the view. Try again.");
    expect(page.getByRole("textbox", { name: "Name" }).element().hasAttribute("aria-invalid")).toBe(false);
  });

  it("disables Save as new view at the limit, and says why", async () => {
    state.limit = 2;
    openAt("?filters=flagged");

    await expect.element(page.getByRole("button", { name: "Save as new view" })).toBeDisabled();
    await expect.element(page.getByTestId("people-views-limit")).toHaveTextContent(
      "You have 2 views, the maximum. Delete a view to save a new view."
    );
  });

  it("offers Save view on a view only with unsaved changes, and saves them over it", async () => {
    calls.update.mockImplementation(async (args) => {
      const { request } = args as { request: { query: typeof lows.view.query } };
      state.views = [{ ...lows, view: { ...lows.view, query: request.query } }, fortnight];
      return {};
    });
    openAt(`?view=${LOWS}`);
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByRole("button", { name: "Save view", exact: true }).elements()).toHaveLength(0);

    await userEvent.type(page.getByTestId("people-search"), "bo");
    await expect.element(page.getByTestId("people-view-unsaved")).toBeVisible();
    await page.getByRole("button", { name: "Save view", exact: true }).click();

    await expect.poll(() => calls.update.mock.calls.length).toBe(1);
    const args = calls.update.mock.calls[0][0] as { viewId: string; request: unknown };
    expect(args.viewId).toBe(LOWS);
    expect(args.request).toEqual({
      name: "Lows",
      query: { search: "bo", ...lows.view.query },
      lens: PeopleLens.Now,
    });
    expect(PeopleViewRequestSchema.safeParse(args.request).success).toBe(true);
    await expect.poll(() => nav.address.search).toBe(`?view=${LOWS}`);
    expect(page.getByTestId("people-view-unsaved").elements()).toHaveLength(0);
  });

  it("keeps what the viewer types while a save runs, as a change over the saved view", async () => {
    let finish = () => {};
    calls.update.mockImplementation(
      (args) =>
        new Promise((resolve) => {
          finish = () => {
            const { request } = args as { request: { query: typeof lows.view.query } };
            state.views = [{ ...lows, view: { ...lows.view, query: request.query } }, fortnight];
            resolve({});
          };
        })
    );
    openAt(`?view=${LOWS}&q=bo`);
    await expect.element(page.getByTestId("people-view-unsaved")).toBeVisible();
    await page.getByRole("button", { name: "Save view", exact: true }).click();
    await expect.poll(() => calls.update.mock.calls.length).toBe(1);

    await userEvent.type(page.getByTestId("people-search"), "b");
    await expect.poll(() => nav.address.searchParams.get("q")).toBe("bob");
    finish();

    await expect.poll(() => state.views?.[0].view.query?.search).toBe("bo");
    await expect.element(page.getByTestId("people-search")).toHaveValue("bob");
    await expect.poll(() => nav.address.search).toBe(`?view=${LOWS}&q=bob`);
    await expect.element(page.getByTestId("people-view-unsaved")).toBeVisible();
  });

  it("leaves the address of another tab alone when a save ends after the viewer left the view", async () => {
    let finish = () => {};
    calls.update.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = () => resolve({});
        })
    );
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt(`?view=${FORTNIGHT}&q=bo`);
    await page.getByRole("button", { name: "Save view", exact: true }).click();
    await expect.poll(() => calls.update.mock.calls.length).toBe(1);

    await tab("Everyone").click();
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=30");
    finish();
    await expect.poll(() => calls.update.mock.settledResults[0]?.type).toBe("fulfilled");
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(nav.address.search).toBe("?lens=review&days=30");
  });

  it("saves a Review view's lens and period", async () => {
    state.review = makeReview(PEOPLE.map((slug) => makeReviewEntry(makePerson({ slug }))));
    openAt(`?view=${FORTNIGHT}`);
    await page.getByRole("radio", { name: "90 days" }).click();
    await expect.poll(() => nav.address.search).toBe(`?view=${FORTNIGHT}&lens=review&days=90`);
    await page.getByRole("button", { name: "Save view", exact: true }).click();

    await expect.poll(() => calls.update.mock.calls.length).toBe(1);
    expect((calls.update.mock.calls[0][0] as { request: unknown }).request).toEqual({
      name: "Fortnight",
      query: { search: "a" },
      lens: PeopleLens.Review,
      reviewDays: 90,
    });
  });
});

describe("a demo subject", () => {
  const clinic = {
    baseDomain: "example.com",
    refusedAsDemoSubject: true,
    refusedStaffWritesAsDemoSubject: false,
    demoPersona: "clinic",
  };

  it("keeps and manages views as the clinic persona, which the demo lets write them", async () => {
    pageState.data = clinic;
    openAt("?filters=flagged");

    await expect.element(page.getByRole("button", { name: "Save as new view" })).toBeEnabled();
    await expect.element(page.getByRole("button", { name: "Manage views" })).toBeEnabled();
    expect(page.getByTestId("people-views-demo").elements()).toHaveLength(0);
  });

  it("disables saving and managing views, and says why, where the demo refuses the staff writes", async () => {
    pageState.data = { ...clinic, refusedStaffWritesAsDemoSubject: true };
    openAt("?filters=flagged");

    await expect.element(page.getByRole("button", { name: "Save as new view" })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Manage views" })).toBeDisabled();
    await expect.element(page.getByTestId("people-views-demo")).toHaveTextContent(
      "You cannot save views in the demo."
    );
  });

  it("disables saving views for the personal persona", async () => {
    pageState.data = {
      baseDomain: "example.com",
      refusedAsDemoSubject: true,
      refusedStaffWritesAsDemoSubject: true,
      demoPersona: null,
    };
    openAt("?filters=flagged");

    await expect.element(page.getByRole("button", { name: "Save as new view" })).toBeDisabled();
  });
});

describe("managing views", () => {
  async function manage() {
    await page.getByRole("button", { name: "Manage views" }).click();
    await expect.element(page.getByTestId("people-views-manager")).toBeVisible();
  }

  it("renames a view, keeping its query and lens", async () => {
    openAt("");
    await manage();
    await page.getByRole("button", { name: "Rename Lows" }).click();
    await page.getByRole("textbox", { name: "Name of the view" }).fill("Lows now");
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await expect.poll(() => calls.update.mock.calls.length).toBe(1);
    const args = calls.update.mock.calls[0][0] as { viewId: string; request: unknown };
    expect(args).toEqual({ viewId: LOWS, request: { name: "Lows now", query: lows.view.query, lens: PeopleLens.Now } });
    expect(PeopleViewRequestSchema.safeParse(args.request).success).toBe(true);
  });

  it("shows the API's reason in place when a new name is taken", async () => {
    calls.update.mockRejectedValue(rejection(409, "people_view_name_taken"));
    openAt("");
    await manage();
    await page.getByRole("button", { name: "Rename Lows" }).click();
    await page.getByRole("textbox", { name: "Name of the view" }).fill("Fortnight");
    await page.getByRole("button", { name: "Save", exact: true }).click();

    const input = page.getByRole("textbox", { name: "Name of the view" });
    await expect.element(input).toHaveAttribute("aria-invalid", "true");
    await expect.element(input).toHaveAccessibleDescription("You have a view with this name. Use a different name.");
    expect(page.getByTestId("people-view-row").first().getByRole("alert").elements()).toHaveLength(1);
  });

  it("moves a view up and down, the first not up and the last not down", async () => {
    openAt("");
    await manage();

    await expect.element(page.getByRole("button", { name: "Move Lows up" })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Move Fortnight down" })).toBeDisabled();
    await page.getByRole("button", { name: "Move Fortnight up" }).click();

    await expect.poll(() => calls.reorder.mock.calls.length).toBe(1);
    const request = calls.reorder.mock.calls[0][0];
    expect(request).toEqual({ viewIds: [FORTNIGHT, LOWS] });
    expect(PeopleViewOrderRequestSchema.safeParse(request).success).toBe(true);
  });

  it("deletes a view only after a confirm, and leaves its tab for everyone", async () => {
    calls.remove.mockImplementation(async () => {
      state.views = [fortnight];
      return {};
    });
    openAt(`?view=${LOWS}`);
    await manage();
    await page.getByRole("button", { name: "Delete Lows" }).click();
    expect(calls.remove).not.toHaveBeenCalled();
    await page.getByRole("button", { name: "Delete view" }).click();

    await expect.poll(() => calls.remove.mock.calls.length).toBe(1);
    expect(calls.remove).toHaveBeenCalledWith(LOWS);
    await expect.poll(() => nav.address.search).toBe("");
    expect(page.getByText(/You have no view with that link/).elements()).toHaveLength(0);
  });
});
