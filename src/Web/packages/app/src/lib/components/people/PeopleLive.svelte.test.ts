import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { flushSync } from "svelte";
import { page as pageState } from "$app/state";
import { makePerson } from "./people-fixtures.js";
import { overviewFeed as feed, overviewSockets as clients } from "./people-socket-fixture.svelte.js";
import { OVERVIEW_PING_DEBOUNCE_MS, OVERVIEW_PING_MAX_WAIT_MS } from "./live-overview.svelte.js";

const refreshes = vi.hoisted(() => ({ count: 0 }));

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getOverview: () => ({
    current: { tenants: [makePerson({ slug: "amy" })] },
    error: undefined,
    loading: false,
    refresh: async () => {
      refreshes.count++;
    },
  }),
  getReview: () => {
    throw new Error("The Now lens reads no statistics");
  },
  getPerson: () => {
    throw new Error("No person is open");
  },
}));

import PeoplePage from "./PeoplePage.svelte";

/** The pinged socket the page opened. */
function ping() {
  clients[0].handlers.overviewUpdate?.();
}

describe("PeoplePage live updates", () => {
  beforeEach(() => {
    pageState.data = { baseDomain: "example.com" };
    feed.connected = false;
    refreshes.count = 0;
    clients.length = 0;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("opens one socket on the overview namespace for the page, and closes it when the page goes", () => {
    const view = render(PeoplePage);
    flushSync();

    expect(clients).toHaveLength(1);
    expect(clients[0].config).toMatchObject({
      namespace: "/overview",
      ticketPath: "/realtime/overview-ticket",
    });
    expect(clients[0].admissions).toBe(1);

    view.unmount();

    expect(clients[0].destroyed).toBe(true);
  });

  it("reads the overview once, a second after a ping", async () => {
    vi.useFakeTimers();
    render(PeoplePage);
    flushSync();

    ping();
    await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS - 1);
    expect(refreshes.count).toBe(0);
    await vi.advanceTimersByTimeAsync(1);

    expect(refreshes.count).toBe(1);
  });

  it("reads the overview once for a burst of pings", async () => {
    vi.useFakeTimers();
    render(PeoplePage);
    flushSync();

    for (let i = 0; i < 6; i++) {
      ping();
      await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS / 4);
    }
    await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS * 3);

    expect(refreshes.count).toBe(1);
  });

  it("still reads within the ceiling while pings never stop", async () => {
    vi.useFakeTimers();
    render(PeoplePage);
    flushSync();

    for (let elapsed = 0; elapsed < OVERVIEW_PING_MAX_WAIT_MS; elapsed += OVERVIEW_PING_DEBOUNCE_MS / 2) {
      ping();
      await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS / 2);
    }

    expect(refreshes.count).toBe(1);
  });

  it("keeps reading the overview every minute without a ping", async () => {
    vi.useFakeTimers();
    render(PeoplePage);
    flushSync();

    await vi.advanceTimersByTimeAsync(60_000);

    expect(refreshes.count).toBe(1);
  });

  it("says readings refresh every minute until the socket is connected, then that they update as they arrive", async () => {
    render(PeoplePage);

    await expect
      .element(page.getByText("1 person. Readings refresh every minute."))
      .toBeVisible();

    feed.connected = true;

    await expect
      .element(page.getByText("1 person. Readings update as they arrive."))
      .toBeVisible();

    feed.connected = false;

    await expect
      .element(page.getByText("1 person. Readings refresh every minute."))
      .toBeVisible();
  });
});
