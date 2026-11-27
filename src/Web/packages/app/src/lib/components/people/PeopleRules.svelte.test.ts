import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { NotificationUrgency, OverviewRuleSet, type TenantOverviewItem } from "@nocturne/api-client";
import { page as pageState } from "$app/state";
import { makePerson } from "./people-fixtures.js";
import { makeRuleReason, ruleAttention } from "./flag-rule-fixtures.js";
import { overviewFeed as feed, overviewSockets as clients } from "./people-socket-fixture.svelte.js";
import { RULES_PENDING_REREAD_MS } from "./people-overview.svelte.js";

const overview = vi.hoisted(() => ({
  tenants: [] as unknown[],
  pending: 0,
  args: [] as unknown[],
  refreshes: 0,
}));

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({ current: [], error: undefined, loading: false, refresh: async () => {} }),
  getOverview: (args: unknown) => {
    overview.args.push(args);
    return {
      get current() {
        return { tenants: overview.tenants, peopleWithRulesPending: overview.pending };
      },
      error: undefined,
      loading: false,
      refresh: async () => {
        overview.refreshes++;
      },
    };
  },
  getReview: () => {
    throw new Error("The Now lens reads no statistics");
  },
  getPerson: () => {
    throw new Error("No person is open");
  },
}));

import PeoplePage from "./PeoplePage.svelte";

describe("PeoplePage and the viewer's own flag rules", () => {
  beforeEach(() => {
    pageState.data = { baseDomain: "example.com" };
    feed.connected = false;
    clients.length = 0;
    overview.tenants = [makePerson({ slug: "amy" })];
    overview.pending = 0;
    overview.args = [];
    overview.refreshes = 0;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("reads the overview with the viewer's own flag rules", async () => {
    render(PeoplePage);
    await expect.element(page.getByText("Amy")).toBeVisible();

    expect(overview.args.length).toBeGreaterThan(0);
    expect(overview.args).toEqual(overview.args.map(() => ({ rules: OverviewRuleSet.All })));
  });

  it("links to the viewer's flag rules", async () => {
    render(PeoplePage);

    await expect.element(page.getByRole("link", { name: "Your flag rules" })).toBeVisible();
  });

  it("reads again sooner while rules are still being checked for some people", async () => {
    vi.useFakeTimers();
    overview.pending = 3;
    render(PeoplePage);
    flushSync();

    await vi.advanceTimersByTimeAsync(RULES_PENDING_REREAD_MS - 1);
    expect(overview.refreshes).toBe(0);
    await vi.advanceTimersByTimeAsync(1);

    expect(overview.refreshes).toBe(1);
  });

  it("does not read again sooner once no one is pending", async () => {
    vi.useFakeTimers();
    render(PeoplePage);
    flushSync();

    await vi.advanceTimersByTimeAsync(RULES_PENDING_REREAD_MS * 4);

    expect(overview.refreshes).toBe(0);
  });

  it("puts a person flagged by a rule in Flagged with the rule's reason", async () => {
    const ruled: TenantOverviewItem = makePerson({
      slug: "ben",
      attention: [ruleAttention(makeRuleReason(), NotificationUrgency.Hazard)],
    });
    overview.tenants = [ruled, makePerson({ slug: "amy" })];
    render(PeoplePage);

    await expect.element(page.getByRole("heading", { name: /Flagged/ })).toBeVisible();
    await expect
      .element(page.getByText("Time below consensus range: 6.5% in the last 14 days (your rule: above 4%)"))
      .toBeVisible();
  });
});
