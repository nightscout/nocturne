import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";
import { flushSync } from "svelte";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import {
  GlucoseStatus,
  NotificationUrgency,
  OverviewRuleSet,
  TenantOverviewAttentionKind,
  type StaffGroup,
  type TenantOverviewItem,
  type TenantOverviewResponse,
  type TenantReviewResponse,
} from "@nocturne/api-client";
import { StaffGroupSchema, TenantOverviewResponseSchema } from "@nocturne/api-client/schemas";
import { OVERVIEW_PING_DEBOUNCE_MS } from "./live-overview.svelte.js";
import { overviewSockets } from "./people-socket-fixture.svelte.js";
import { makePerson, makeReview, makeReviewEntry } from "./people-fixtures.js";

const EVERYONE = "everyone";

const state = $state<{
  groups: StaffGroup[] | undefined;
  groupsError: unknown;
  reads: Record<string, TenantOverviewResponse | undefined>;
  errors: Record<string, unknown>;
  reviews: Record<string, TenantReviewResponse | undefined>;
}>({ groups: [], groupsError: undefined, reads: {}, errors: {}, reviews: {} });

/**
 * What the API answers to the next read of the groups. While `held` is set, each read waits for the
 * test to answer it.
 */
const server = {
  groups: [] as StaffGroup[],
  held: undefined as ((groups: StaffGroup[]) => void)[] | undefined,
};

const calls = vi.hoisted(() => ({
  overview: [] as unknown[],
  review: [] as unknown[],
  refreshes: {} as Record<string, number>,
  reviewRefreshes: {} as Record<string, number>,
  groupReads: 0,
  polls: [] as (() => void)[],
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

vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({
  pollWhileVisible: (tick: () => void) => {
    calls.polls.push(tick);
  },
}));

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({
    get current() {
      return state.groups;
    },
    get error() {
      return state.groupsError;
    },
    loading: false,
    refresh: async () => {
      calls.groupReads++;
      const held = server.held;
      if (held) {
        state.groups = await new Promise<StaffGroup[]>((answer) => held.push(answer));
        return;
      }
      await Promise.resolve();
      state.groups = server.groups;
    },
  }),
  getOverview: (args?: { group?: string; rules?: OverviewRuleSet }) => {
    calls.overview.push(args);
    const key = args?.group ?? EVERYONE;
    return {
      get current() {
        return state.reads[key];
      },
      get error() {
        return state.errors[key];
      },
      loading: false,
      refresh: async () => {
        calls.refreshes[key] = (calls.refreshes[key] ?? 0) + 1;
      },
    };
  },
  getReview: (args: { group?: string; days: number }) => {
    calls.review.push(args);
    const key = `${args.group ?? EVERYONE} ${args.days}`;
    return {
      get current() {
        return state.reviews[key];
      },
      error: undefined,
      get loading() {
        return !state.reviews[key];
      },
      refresh: async () => {
        calls.reviewRefreshes[key] = (calls.reviewRefreshes[key] ?? 0) + 1;
      },
    };
  },
  getPerson: () => ({ current: undefined, error: undefined, loading: true, refresh: async () => {} }),
}));

import PeoplePage from "./PeoplePage.svelte";

function flagged(slug: string): TenantOverviewItem {
  return makePerson({
    slug,
    status: GlucoseStatus.Low,
    attention: [
      { kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent },
    ],
  });
}

const birch: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000001", name: "Birch cabin", teamName: "Camp Wren", sharesName: false };
const oak: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000002", name: "Oak cabin", teamName: "Camp Wren", sharesName: false };
const pine: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000003", name: "Pine cabin", teamName: "Camp Wren", sharesName: false };
const spruce: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000004", name: "Spruce cabin", teamName: "Camp Wren", sharesName: false };

function notFound() {
  try {
    error(404, "Not found");
  } catch (thrown) {
    return thrown;
  }
}

/** Holds each read of the groups until the test answers it, in the order the reads began. */
function holdGroupReads() {
  const held: ((groups: StaffGroup[]) => void)[] = [];
  server.held = held;
  return held;
}

function openAt(search: string) {
  pageState.url = new URL(`http://localhost/${search}`);
  nav.address = new URL(pageState.url.href);
  return render(PeoplePage);
}

function names() {
  return page
    .getByTestId("person-row")
    .elements()
    .map((row) => row.querySelector("p")?.textContent?.trim());
}

const tab = (name: string | RegExp) => page.getByRole("tab", { name });

beforeEach(async () => {
  await page.viewport(1100, 900);
  pageState.data = { baseDomain: "example.com" };
  state.groups = [birch, oak];
  server.groups = [birch, oak];
  server.held = undefined;
  state.groupsError = undefined;
  state.errors = {};
  state.reviews = {};
  state.reads = {
    [EVERYONE]: {
      tenants: [flagged("amy"), makePerson({ slug: "bo" }), makePerson({ slug: "cy" }), makePerson({ slug: "di" }), makePerson({ slug: "ed" })],
    },
    [birch.id!]: {
      tenants: [flagged("amy"), makePerson({ slug: "cy" }), makePerson({ slug: "di" }), makePerson({ slug: "ed" }), makePerson({ slug: "fay" })],
      hiddenCount: 0,
    },
    [oak.id!]: { tenants: [makePerson({ slug: "bo" })], hiddenCount: 0 },
    [pine.id!]: { tenants: [makePerson({ slug: "gil" })], hiddenCount: 0 },
    [spruce.id!]: { tenants: [makePerson({ slug: "hal" })], hiddenCount: 0 },
  };
  calls.overview.length = 0;
  calls.review.length = 0;
  calls.refreshes = {};
  calls.reviewRefreshes = {};
  calls.groupReads = 0;
  calls.polls.length = 0;
  overviewSockets.length = 0;
});

afterEach(() => {
  vi.useRealTimers();
});

describe("PeoplePage group tabs", () => {
  it("shows no tab strip for a viewer in no group", async () => {
    state.groups = [];
    openAt("");

    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByRole("tablist").elements()).toHaveLength(0);
  });

  it("lists Everyone, then each group by the API's order, with Everyone selected", async () => {
    openAt("");

    await expect.element(page.getByRole("tablist", { name: "Lists of people" })).toBeVisible();
    expect(page.getByRole("tab").elements().map((t) => t.textContent?.trim())).toEqual([
      "Everyone",
      "Birch cabin",
      "Oak cabin",
    ]);
    await expect.element(tab("Everyone")).toHaveAttribute("aria-selected", "true");
    expect(calls.overview).toContainEqual({ rules: OverviewRuleSet.All });
  });

  it("names the team on the tabs of two groups that share a name", async () => {
    state.groups = [
      { id: "g-1", name: "Ward", teamName: "North clinic", sharesName: true },
      { id: "g-2", name: "Ward", teamName: "South clinic", sharesName: true },
      birch,
    ];
    openAt("");

    await expect.element(tab("Ward, North clinic")).toBeVisible();
    await expect.element(tab("Ward, South clinic")).toBeVisible();
    await expect.element(tab("Ward, North clinic")).toHaveTextContent(/Ward\s*·\s*North clinic/);
    await expect.element(tab("Birch cabin")).toBeVisible();
    expect(page.getByText("Camp Wren").elements()).toHaveLength(0);
  });

  it("reads a group's people for its tab and shows them in the API's order with their own Flagged section", async () => {
    openAt("");
    await expect.element(page.getByText("Bo")).toBeVisible();

    await tab("Birch cabin").click();

    await expect.element(page.getByText("Fay")).toBeVisible();
    expect(calls.overview).toContainEqual({ group: "0193b6a0-0000-7000-8000-000000000001", rules: OverviewRuleSet.All });
    await expect.element(tab("Birch cabin")).toHaveAttribute("aria-selected", "true");
    await expect.element(page.getByRole("heading", { name: /Flagged\s*1/ })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: /Other people\s*4/ })).toBeVisible();
    expect(names()).toEqual(["Amy", "Cy", "Di", "Ed", "Fay"]);
    expect(page.getByText("Bo").elements()).toHaveLength(0);
    expect(nav.address.searchParams.get("group")).toBe("0193b6a0-0000-7000-8000-000000000001");
  });

  it("reads the statistics of the open group in the Review lens", async () => {
    openAt("?group=0193b6a0-0000-7000-8000-000000000002&lens=review&days=30");

    await expect.element(page.getByText("Bo")).toBeVisible();
    expect(calls.review).toContainEqual({ group: "0193b6a0-0000-7000-8000-000000000002", rules: OverviewRuleSet.All, days: 30 });
  });

  it("states how many people of the group the viewer cannot see, for one and for many", async () => {
    state.reads[oak.id!] = { tenants: [makePerson({ slug: "bo" })], hiddenCount: 1 };
    state.reads[birch.id!] = { tenants: [makePerson({ slug: "cy" })], hiddenCount: 3 };
    openAt("?group=0193b6a0-0000-7000-8000-000000000002");

    await expect.element(page.getByText("You cannot see 1 person in this group.")).toBeVisible();

    await tab("Birch cabin").click();
    await expect.element(page.getByText("You cannot see 3 people in this group.")).toBeVisible();

    await tab("Everyone").click();
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByText(/You cannot see/).elements()).toHaveLength(0);
  });

  it("says the sites keep the people out on a group tab where the viewer can see nobody of those it holds", async () => {
    state.reads[oak.id!] = { tenants: [], hiddenCount: 2 };
    openAt("?group=0193b6a0-0000-7000-8000-000000000002");

    await expect.element(page.getByText("You cannot see anyone in this group")).toBeVisible();
    await expect
      .element(page.getByText("Each person's site decides who on the team can see them."))
      .toBeVisible();
    await expect.element(page.getByText("You cannot see 2 people in this group.")).toBeVisible();
    expect(page.getByText("You follow no other people").elements()).toHaveLength(0);
    expect(page.getByText("You can see no people in this group").elements()).toHaveLength(0);
  });

  it("says a manager chooses the people on a group tab that holds nobody", async () => {
    state.reads[oak.id!] = { tenants: [], hiddenCount: 0 };
    openAt("?group=0193b6a0-0000-7000-8000-000000000002");

    await expect.element(page.getByText("You can see no people in this group")).toBeVisible();
    await expect
      .element(page.getByText("A manager of the team chooses the people in each group."))
      .toBeVisible();
    expect(page.getByText(/You cannot see/).elements()).toHaveLength(0);
  });

  it("opens the group a link names, keeps it through tab changes, and follows a navigation back", async () => {
    openAt("?group=0193b6a0-0000-7000-8000-000000000002&lens=review&days=30&person=bo");

    await expect.element(tab("Oak cabin")).toHaveAttribute("aria-selected", "true");
    expect(calls.overview).toContainEqual({ group: "0193b6a0-0000-7000-8000-000000000002", rules: OverviewRuleSet.All });

    await tab("Everyone").click();
    await expect.element(tab("Everyone")).toHaveAttribute("aria-selected", "true");
    expect(nav.address.searchParams.get("group")).toBeNull();
    expect(nav.address.searchParams.get("lens")).toBe("review");
    expect(nav.address.searchParams.get("days")).toBe("30");
    expect(nav.address.searchParams.get("person")).toBe("bo");

    await tab("Birch cabin").click();
    expect(nav.address.searchParams.get("group")).toBe("0193b6a0-0000-7000-8000-000000000001");
    expect(nav.address.searchParams.get("lens")).toBe("review");

    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000002");
    nav.afterNavigate();
    await expect.element(tab("Oak cabin")).toHaveAttribute("aria-selected", "true");
    await expect.element(page.getByText("Bo")).toBeVisible();
  });

  it("shows everyone, with a message, for a group the viewer is not in, and drops it from the address", async () => {
    openAt("?group=0193b6a0-0000-7000-8000-000000000009&lens=review&days=30");

    await expect
      .element(page.getByText("You are not in that group. The list shows everyone you follow."))
      .toBeVisible();
    await expect.element(tab("Everyone")).toHaveAttribute("aria-selected", "true");
    expect(nav.address.searchParams.get("group")).toBeNull();
    expect(nav.address.searchParams.get("lens")).toBe("review");

    await tab("Oak cabin").click();
    await expect.element(page.getByText("Bo")).toBeVisible();
    expect(page.getByText(/You are not in that group/).elements()).toHaveLength(0);
  });

  it("falls back to everyone when the group's read answers 404", async () => {
    state.errors[oak.id!] = notFound();
    openAt("?group=0193b6a0-0000-7000-8000-000000000002");

    await expect
      .element(page.getByText("You are not in that group. The list shows everyone you follow."))
      .toBeVisible();
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(nav.address.searchParams.get("group")).toBeNull();
  });

  it("shows everyone, with a message, and drops the group from the address when the groups do not load", async () => {
    state.groups = undefined;
    state.groupsError = new Error("offline");
    openAt("?group=0193b6a0-0000-7000-8000-000000000002&lens=review&days=30");

    await expect
      .element(page.getByText("Your groups did not load. The list shows everyone you follow."))
      .toBeVisible();
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByRole("tablist").elements()).toHaveLength(0);
    expect(nav.address.searchParams.get("group")).toBeNull();
    expect(nav.address.searchParams.get("lens")).toBe("review");
    expect(calls.overview.at(-1)).toEqual({ rules: OverviewRuleSet.All });
  });

  it("reads the groups again on the overview's poll, so a group added while the page is open gains a tab", async () => {
    openAt("");
    await expect.element(tab("Oak cabin")).toBeVisible();
    expect(page.getByRole("tab", { name: "Pine cabin" }).elements()).toHaveLength(0);

    server.groups = [birch, oak, pine];
    for (const tick of calls.polls) tick();

    await expect.element(tab("Pine cabin")).toBeVisible();
    expect(calls.groupReads).toBe(1);
    expect(calls.refreshes).toEqual({ [EVERYONE]: 1 });
  });

  it("reads the groups again before it refuses a group a navigation names", async () => {
    openAt("");
    await expect.element(tab("Oak cabin")).toBeVisible();

    server.groups = [birch, oak, pine];
    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000003");
    nav.afterNavigate();

    await expect.element(tab("Pine cabin")).toHaveAttribute("aria-selected", "true");
    await expect.element(page.getByText("Gil")).toBeVisible();
    expect(calls.groupReads).toBe(1);
    expect(page.getByText(/You are not in that group/).elements()).toHaveLength(0);
  });

  it("still refuses a group a navigation names when the groups read again do not list it", async () => {
    openAt("");
    await expect.element(tab("Oak cabin")).toBeVisible();

    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000003");
    nav.afterNavigate();

    await expect
      .element(page.getByText("You are not in that group. The list shows everyone you follow."))
      .toBeVisible();
    expect(calls.groupReads).toBe(1);
    await expect.element(tab("Everyone")).toHaveAttribute("aria-selected", "true");
  });

  it("shows the group's 404 as loading while the groups are read again, then falls back", async () => {
    const held = holdGroupReads();
    state.reads[pine.id!] = undefined;
    state.errors[pine.id!] = notFound();
    openAt("");
    await expect.element(tab("Oak cabin")).toBeVisible();

    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000003");
    nav.afterNavigate();

    await expect.element(page.getByText("Loading…")).toBeVisible();
    expect(page.getByText("The list of people did not load.").elements()).toHaveLength(0);
    expect(page.getByText(/You are not in that group/).elements()).toHaveLength(0);

    held[0]([birch, oak]);

    await expect
      .element(page.getByText("You are not in that group. The list shows everyone you follow."))
      .toBeVisible();
    expect(page.getByText("The list of people did not load.").elements()).toHaveLength(0);
    await expect.element(page.getByText("Amy")).toBeVisible();
  });

  it("judges a second navigation against the groups its own read gives, not the first read's", async () => {
    const held = holdGroupReads();
    openAt("");
    await expect.element(tab("Oak cabin")).toBeVisible();

    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000003");
    nav.afterNavigate();
    pageState.url = new URL("http://localhost/?group=0193b6a0-0000-7000-8000-000000000004");
    nav.afterNavigate();
    await expect.poll(() => held.length).toBe(2);

    held[0]([birch, oak, pine]);
    await expect.element(tab("Pine cabin")).toBeVisible();
    await new Promise((settled) => setTimeout(settled, 0));
    flushSync();
    expect(page.getByText(/You are not in that group/).elements()).toHaveLength(0);
    await expect.element(page.getByText("Hal")).toBeVisible();

    held[1]([birch, oak, pine, spruce]);

    await expect.element(tab("Spruce cabin")).toHaveAttribute("aria-selected", "true");
    await expect.element(page.getByText("Hal")).toBeVisible();
    expect(page.getByText(/You are not in that group/).elements()).toHaveLength(0);
  });

  it("reads a person's statistics again on Everyone after it read them again on a group tab", async () => {
    const amy = flagged("amy");
    const cy = makePerson({ slug: "cy" });
    state.reads[birch.id!] = { tenants: [amy, cy], hiddenCount: 0 };
    state.reads[EVERYONE] = { tenants: [amy, cy] };
    state.reviews[`${birch.id} 14`] = makeReview([makeReviewEntry(amy)]);
    state.reviews[`${EVERYONE} 14`] = makeReview([makeReviewEntry(amy)]);
    openAt("?group=0193b6a0-0000-7000-8000-000000000001&lens=review&days=14");

    await expect.poll(() => calls.reviewRefreshes[`${birch.id} 14`]).toBe(1);

    await tab("Everyone").click();

    await expect.poll(() => calls.reviewRefreshes[`${EVERYONE} 14`]).toBe(1);
    expect(calls.reviewRefreshes[`${birch.id} 14`]).toBe(1);
  });

  it("reads the open tab again on a ping, and not everyone's", async () => {
    vi.useFakeTimers();
    openAt("?group=0193b6a0-0000-7000-8000-000000000001");
    flushSync();

    overviewSockets[0].handlers.overviewUpdate?.();
    await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS);

    expect(calls.refreshes).toEqual({ [birch.id!]: 1 });
  });

  it("moves between tabs with the arrow keys", async () => {
    openAt("");
    await expect.element(tab("Everyone")).toBeVisible();

    tab("Everyone").element().focus();
    await userEvent.keyboard("{ArrowRight}");

    await expect.element(tab("Birch cabin")).toHaveAttribute("aria-selected", "true");
    await expect.element(tab("Birch cabin")).toHaveFocus();
    await expect.element(page.getByText("Fay")).toBeVisible();
    expect(nav.address.searchParams.get("group")).toBe("0193b6a0-0000-7000-8000-000000000001");
  });
});

describe("the wall display link", () => {
  it("opens the wall on the open tab", async () => {
    openAt(`?group=${birch.id}`);
    const link = page.getByRole("link", { name: "Wall display" });
    await expect.element(link).toHaveAttribute("href", `/people/wall?group=${birch.id}`);

    await tab("Everyone").click();
    await expect.element(link).toHaveAttribute("href", "/people/wall");

    await tab("Oak cabin").click();
    await expect.element(link).toHaveAttribute("href", `/people/wall?group=${oak.id}`);
  });
});

describe("the overview and groups responses through their generated schemas", () => {
  it("turns a null hidden count into none, which the page reads as no sentence, and keeps a number", async () => {
    const everyone = TenantOverviewResponseSchema.parse({ tenants: [], hiddenCount: null });
    expect(everyone.hiddenCount).toBeUndefined();
    const group = TenantOverviewResponseSchema.parse({ tenants: [], hiddenCount: 2 });
    expect(group.hiddenCount).toBe(2);
    expect(StaffGroupSchema.parse(birch)).toEqual(birch);

    state.reads[EVERYONE] = { ...everyone, tenants: [makePerson({ slug: "amy" })] };
    openAt("");
    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByText(/You cannot see/).elements()).toHaveLength(0);
  });
});
