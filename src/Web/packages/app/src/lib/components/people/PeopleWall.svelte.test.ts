import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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
} from "@nocturne/api-client";
import { TenantOverviewResponseSchema } from "@nocturne/api-client/schemas";
import { bg, bgLabel, time } from "#lib/utils/formatting.js";
import { wallStep } from "./wall-layout.js";
import { OVERLAY_REVEAL_MS } from "#lib/utils/overlay-reveal.svelte.js";
import { OVERVIEW_PING_DEBOUNCE_MS } from "./live-overview.svelte.js";
import { makePerson } from "./people-fixtures.js";
import { overviewFeed as feed, overviewSockets as clients } from "./people-socket-fixture.svelte.js";

/** What the API answers: `data` and `error` for everyone, `groupReads` and `groupErrors` for each group. */
const overview = $state<{
  data: TenantOverviewResponse | undefined;
  error: unknown;
  groups: StaffGroup[] | undefined;
  groupsError: unknown;
  groupReads: Record<string, TenantOverviewResponse | undefined>;
  groupErrors: Record<string, unknown>;
}>({ data: undefined, error: undefined, groups: [], groupsError: undefined, groupReads: {}, groupErrors: {} });
const refreshes = vi.hoisted(() => ({ count: 0, run: async () => {}, reads: [] as unknown[] }));
const navigation = vi.hoisted(() => ({
  goto: vi.fn(async () => {}),
  afterNavigate: (callback: (navigation: unknown) => void) => {
    navigation.arrived = callback;
  },
  arrived: (_navigation: unknown) => {},
  replaceState: (url: string | URL) => {
    navigation.address = new URL(url, navigation.address.href);
  },
  address: new URL("http://localhost/people/wall"),
}));

vi.mock("$app/navigation", () => navigation);
vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({ pollWhileVisible: () => {} }));
vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));
vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({
    get current() {
      return overview.groups;
    },
    get error() {
      return overview.groupsError;
    },
    loading: false,
    refresh: async () => {},
  }),
  getOverview: (args?: { group?: string; rules?: OverviewRuleSet }) => {
    refreshes.reads.push(args);
    const group = args?.group;
    return {
      get current() {
        return group ? overview.groupReads[group] : overview.data;
      },
      get error() {
        return group ? overview.groupErrors[group] : overview.error;
      },
      loading: false,
      refresh: async () => {
        refreshes.count++;
        await refreshes.run();
      },
    };
  },
}));

import PeopleWall from "./PeopleWall.svelte";

function named(index: number, overrides: Partial<TenantOverviewItem> = {}) {
  const label = String(index).padStart(3, "0");
  return makePerson({ slug: `p${label}`, displayName: `Person ${label}`, ...overrides });
}

function flagged(index: number, overrides: Partial<TenantOverviewItem> = {}) {
  return named(index, {
    status: GlucoseStatus.Low,
    attention: [
      { kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent },
      { kind: TenantOverviewAttentionKind.ActiveAlert, level: NotificationUrgency.Warn },
    ],
    activeAlertCount: 1,
    ...overrides,
  });
}

function flaggedMany(index: number) {
  return flagged(index, {
    attention: [
      { kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent },
      { kind: TenantOverviewAttentionKind.ActiveAlert, level: NotificationUrgency.Warn },
      { kind: TenantOverviewAttentionKind.TrackerDue, level: NotificationUrgency.Warn, trackerName: "Sensor", hoursUntilEvent: 0 },
    ],
  });
}

function crowd(total: number, flaggedCount: number) {
  return Array.from({ length: total }, (_, i) => (i < flaggedCount ? flagged(i) : named(i)));
}

function namesIn(testId: string) {
  return page
    .getByTestId(testId)
    .elements()
    .flatMap((list) => [...list.querySelectorAll("p[title]")].map((p) => p.textContent?.trim()));
}

function allNameElements() {
  return [...document.querySelectorAll<HTMLElement>("[data-testid='people-wall'] p[title]")];
}

function clippedCards() {
  const clip = page.getByTestId("wall-band-box").element().getBoundingClientRect();
  return page
    .getByTestId("wall-flagged")
    .elements()
    .filter((card) => card.getBoundingClientRect().bottom > clip.bottom + 1).length;
}

const wall = () => page.getByTestId("people-wall");

/** Puts the wall's address at `/people/wall` with `search`; the wall reads it when it is drawn. */
function openAt(search: string) {
  pageState.url = new URL(`http://localhost/people/wall${search}`);
  navigation.address = new URL(pageState.url.href);
}

beforeEach(async () => {
  await page.viewport(1280, 720);
  overview.data = undefined;
  overview.error = undefined;
  overview.groups = [];
  overview.groupsError = undefined;
  overview.groupReads = {};
  overview.groupErrors = {};
  feed.connected = false;
  refreshes.count = 0;
  refreshes.run = async () => {};
  refreshes.reads.length = 0;
  clients.length = 0;
  navigation.goto.mockClear();
  navigation.arrived = () => {};
  pageState.shallow = null;
  openAt("");
});

afterEach(() => {
  vi.useRealTimers();
});

describe("People wall display", () => {
  it("reads the overview with its teams' rules and without the viewer's own, since the room sees it", async () => {
    overview.data = { tenants: [named(1)] };
    render(PeopleWall);
    await expect.element(page.getByText("Person 001")).toBeVisible();

    expect(refreshes.reads.length).toBeGreaterThan(0);
    expect(refreshes.reads).toEqual(refreshes.reads.map(() => ({ rules: OverviewRuleSet.Team })));
  });

  it("states the fixed rules alone when no team's rules applied", async () => {
    overview.data = { tenants: [named(1)], ruleTeams: [] };
    render(PeopleWall);

    await expect.element(page.getByTestId("wall-rules")).toHaveTextContent("Flagged by Nocturne's fixed rules.");
  });

  it("names the one team whose rules applied", async () => {
    overview.data = { tenants: [named(1)], ruleTeams: [{ id: birch.id, name: "Ward 4" }] };
    render(PeopleWall);

    await expect
      .element(page.getByTestId("wall-rules"))
      .toHaveTextContent("Flagged by Nocturne's fixed rules and Ward 4's rules.");
  });

  it("counts and names every team whose rules applied, in the order the API gives", async () => {
    overview.data = {
      tenants: [named(1)],
      ruleTeams: [
        { id: birch.id, name: "Ward 4" },
        { id: oak.id, name: "Ward 5" },
        { id: "0193b6a0-0000-7000-8000-0000000000c3", name: "Clinic" },
      ],
    };
    render(PeopleWall);

    await expect
      .element(page.getByTestId("wall-rules"))
      .toHaveTextContent("Flagged by Nocturne's fixed rules and the rules of 3 teams: Ward 4, Ward 5, and Clinic.");
  });

  describe("leaving", () => {
    it("goes back to the People page on Escape", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 001")).toBeVisible();

      await userEvent.keyboard("{Escape}");

      expect(navigation.goto).toHaveBeenCalledWith("/people", { replaceState: true });
    });

    it("goes back in history, adding no entry, when the wall was opened from the People page", async () => {
      const back = vi.spyOn(history, "back").mockImplementation(() => {});
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 001")).toBeVisible();
      navigation.arrived({ from: { route: { id: "/(authenticated)/people" } } });

      await userEvent.keyboard("{Escape}");

      expect(back).toHaveBeenCalledTimes(1);
      expect(navigation.goto).not.toHaveBeenCalled();
      back.mockRestore();
    });

    it("reveals Back when the pointer moves, hides it again, and goes back on a press", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);
      const nav = page.getByTestId("wall-nav");
      await expect.element(nav).toHaveAttribute("data-revealed", "false");

      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      window.dispatchEvent(new PointerEvent("pointermove", { pointerType: "mouse" }));
      await expect.element(nav).toHaveAttribute("data-revealed", "true");

      await vi.advanceTimersByTimeAsync(OVERLAY_REVEAL_MS + 1);
      await expect.element(nav).toHaveAttribute("data-revealed", "false");
      vi.useRealTimers();

      await page.getByRole("button", { name: "Back" }).click({ force: true });
      expect(navigation.goto).toHaveBeenCalledWith("/people", { replaceState: true });
    });
  });

  describe("people", () => {
    it("puts flagged people across the top and everyone else in columns, each in the API's order", async () => {
      overview.data = {
        tenants: [named(1), flagged(2), named(3), flagged(4), named(5)],
      };
      render(PeopleWall);
      await expect.element(page.getByText("Person 005")).toBeVisible();

      expect(namesIn("wall-band")).toEqual(["Person 002", "Person 004"]);
      expect(namesIn("wall-columns")).toEqual(["Person 001", "Person 003", "Person 005"]);
    });

    it("gives a flagged person the reading with its unit, the reasons in words and the age", async () => {
      overview.data = { tenants: [flagged(1)] };
      render(PeopleWall);

      const card = page.getByTestId("wall-flagged");
      await expect.element(card).toBeVisible();
      await expect.element(card.getByTestId("bg-unit")).toHaveTextContent(bgLabel());
      await expect.element(card.getByText("Low")).toBeVisible();
      await expect.element(card.getByText("1 active alert")).toBeVisible();
      await expect.element(card.getByTestId("reading-age")).toHaveTextContent("5m ago");
    });

    it("spreads a short list over every column, still in the API's order", async () => {
      await page.viewport(1280, 720);
      overview.data = { tenants: Array.from({ length: 8 }, (_, i) => named(i + 1)) };
      render(PeopleWall);
      await expect.element(page.getByText("Person 008")).toBeVisible();

      const lists = () => page.getByTestId("wall-columns").element().querySelectorAll(":scope > ul");
      await expect.poll(() => lists().length).toBe(4);
      expect(namesIn("wall-columns")).toEqual(
        Array.from({ length: 8 }, (_, i) => `Person 00${i + 1}`)
      );
    });

    it("leaves out the top band when nobody is flagged", async () => {
      overview.data = { tenants: [named(1), named(2)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 002")).toBeVisible();

      expect(page.getByTestId("wall-band").elements()).toHaveLength(0);
    });

    it("says so when the viewer follows nobody", async () => {
      overview.data = { tenants: [] };
      render(PeopleWall);

      await expect.element(page.getByText("You do not follow anyone.")).toBeVisible();
    });

    it("takes a stale reading out of its range colour and shows its age", async () => {
      overview.data = {
        tenants: [
          named(1),
          named(2, {
            status: GlucoseStatus.Stale,
            lastReadingAt: new Date(Date.now() - 3 * 60 * 60_000).toISOString(),
          }),
        ],
      };
      render(PeopleWall);
      await expect.element(page.getByText("Person 002")).toBeVisible();

      const [current, stale] = page.getByTestId("wall-line").elements();
      expect(current.innerHTML).toContain("bg-glucose-in-range");
      expect(stale.innerHTML).not.toContain("bg-glucose-");
      expect(stale.querySelector("[data-testid='reading-age']")?.textContent?.trim()).toBe("3h ago");
    });

    it("shows No reading for a person with no reading", async () => {
      overview.data = {
        tenants: [
          named(1, { latest: undefined, lastReadingAt: undefined, status: GlucoseStatus.Unknown }),
        ],
      };
      render(PeopleWall);

      await expect.element(page.getByTestId("wall-line").getByText("No reading")).toBeVisible();
    });
  });

  describe("legend", () => {
    it("names each range and gives its bounds in the user's unit when everyone shares thresholds", async () => {
      overview.data = { tenants: [named(1), named(2)] };
      render(PeopleWall);

      const legend = page.getByTestId("wall-legend");
      await expect.element(legend).toBeVisible();
      const text = legend.element().textContent ?? "";
      for (const phrase of [
        "Very low",
        `Below ${bg(54)}`,
        `${bg(54)} to ${bg(70)}`,
        "In range",
        `${bg(70)} to ${bg(180)}`,
        `${bg(180)} to ${bg(250)}`,
        `Above ${bg(250)}`,
        "Very high",
        bgLabel(),
      ]) {
        expect(text).toContain(phrase);
      }
      expect(text).not.toContain("own");
    });

    it("names the ranges without bounds when thresholds differ", async () => {
      overview.data = {
        tenants: [
          named(1),
          named(2, { thresholds: { urgentLow: 50, low: 65, high: 160, urgentHigh: 250 } }),
        ],
      };
      render(PeopleWall);

      const legend = page.getByTestId("wall-legend");
      await expect.element(legend.getByText("Ranges are each person's own.")).toBeVisible();
      const text = legend.element().textContent ?? "";
      for (const name of ["Very low", "Low", "In range", "High", "Very high"]) {
        expect(text).toContain(name);
      }
      expect(text).not.toMatch(/\d+ to \d+|Below|Above/);
    });
  });

  describe("connection", () => {
    it("says readings update as they arrive while the feed is connected", async () => {
      overview.data = { tenants: [named(1)] };
      feed.connected = true;
      render(PeopleWall);

      await expect.element(page.getByText("Readings update as they arrive.")).toBeVisible();
    });

    it("says readings refresh every minute while the feed is not connected", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);

      await expect.element(page.getByText("Readings refresh every minute.")).toBeVisible();
    });

    it("states the time of the last good read when a refresh fails, and keeps the people on screen", async () => {
      vi.useFakeTimers({ toFake: ["Date"] });
      const readAt = new Date("2026-03-04T10:15:00");
      vi.setSystemTime(readAt);
      overview.data = { tenants: [named(1), flagged(2)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 001")).toBeVisible();

      vi.setSystemTime(new Date("2026-03-04T10:40:00"));
      overview.error = new Error("The read failed.");

      await expect
        .element(page.getByText(`Connection lost. Showing readings from ${time(readAt)}.`))
        .toBeVisible();
      await expect.element(page.getByText("Person 001")).toBeVisible();
      await expect.element(page.getByText("Person 002")).toBeVisible();
    });

    it("keeps the last people on screen when the failed refresh also dropped the value", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 001")).toBeVisible();

      overview.data = undefined;
      overview.error = new Error("The read failed.");

      await expect.element(page.getByText(/^Connection lost\./)).toBeVisible();
      await expect.element(page.getByText("Person 001")).toBeVisible();
    });

    it("offers a retry when the first read failed and there is nothing to show", async () => {
      overview.error = new Error("The read failed.");
      render(PeopleWall);

      await expect.element(page.getByText("The list of people did not load.")).toBeVisible();
      await page.getByRole("button", { name: "Try again" }).click();

      expect(refreshes.count).toBe(1);
    });

    it("shows the clock in the user's format", async () => {
      vi.useFakeTimers({ toFake: ["Date"] });
      vi.setSystemTime(new Date("2026-03-04T21:07:09"));
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);

      await expect
        .element(page.getByTestId("wall-clock"))
        .toHaveTextContent(time(new Date("2026-03-04T21:07:09"), { seconds: true }));
    });
  });

  describe("live updates", () => {
    it("shows the new reading after an overviewUpdate ping, with no reload", async () => {
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      overview.data = { tenants: [named(1)] };
      refreshes.run = async () => {
        overview.data = {
          tenants: [
            named(1, { latest: { mgdl: 95, delta: 1, timestamp: new Date().toISOString() } }),
          ],
        };
      };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-line")).toHaveTextContent(bg(120));

      clients[0].handlers.overviewUpdate?.();
      await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS);

      await expect.element(page.getByTestId("wall-line")).toHaveTextContent(bg(95));
      expect(refreshes.count).toBe(1);
    });
  });

  describe("honest readings", () => {
    it("draws kept readings as disconnected and not current after a failed refresh, with their age", async () => {
      overview.data = { tenants: [named(1), flagged(2)] };
      render(PeopleWall);
      await expect.element(page.getByText("Person 001")).toBeVisible();
      const line = page.getByTestId("wall-line").element();
      expect(line.innerHTML).toContain("bg-glucose-in-range");
      expect(line.querySelector("[role='img']")).not.toBeNull();

      overview.error = new Error("The read failed.");

      await expect.element(page.getByText(/^Connection lost\./)).toBeVisible();
      for (const kept of [
        page.getByTestId("wall-line").element(),
        page.getByTestId("wall-flagged").element(),
      ]) {
        expect(kept.innerHTML).not.toContain("bg-glucose-");
        expect(kept.querySelector("[role='img']")).toBeNull();
        expect(kept.innerHTML).toContain("border-dashed");
        expect(kept.querySelector("[data-testid='reading-age']")?.textContent?.trim()).toBe(
          "5m ago"
        );
      }
    });

    it("shows the age on a column line with a current reading", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);

      await expect
        .element(page.getByTestId("wall-line").getByTestId("reading-age"))
        .toHaveTextContent("5m ago");
    });

    it("advances an age with the clock, with no new read", async () => {
      vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
      vi.setSystemTime(new Date("2026-03-04T10:00:00"));
      overview.data = {
        tenants: [named(1, { lastReadingAt: new Date("2026-03-04T09:55:00").toISOString() })],
      };
      render(PeopleWall);
      const age = page.getByTestId("wall-line").getByTestId("reading-age");
      await expect.element(age).toHaveTextContent("5m ago");

      await vi.advanceTimersByTimeAsync(10 * 60_000);

      await expect.element(age).toHaveTextContent("15m ago");
      expect(refreshes.count).toBe(0);
    });

    it("says it is loading, and claims no poll, until the first read answers", async () => {
      render(PeopleWall);

      await expect.element(page.getByText("Loading the list of people.")).toBeVisible();
      expect(page.getByText("Readings refresh every minute.").elements()).toHaveLength(0);
    });

    it("names the wall as the main landmark and dates the clock", async () => {
      overview.data = { tenants: [named(1)] };
      render(PeopleWall);

      await expect.element(page.getByRole("main", { name: "People wall display" })).toBeVisible();
      expect(page.getByTestId("wall-clock").element().getAttribute("datetime")).toMatch(
        /^\d{4}-\d\d-\d\dT/
      );
    });
  });

  describe("flagged people past the band", () => {
    it("marks a flagged person in the columns with an icon and the first reason", async () => {
      overview.data = { tenants: crowd(8, 8) };
      render(PeopleWall);
      await expect.element(page.getByText("Person 007")).toBeVisible();

      const last = page.getByTestId("wall-line").last().element();
      expect(last.querySelector("[data-testid='attention-reasons'] svg")).not.toBeNull();
      expect(last.querySelector("[data-testid='attention-reasons']")?.textContent).toContain(
        "Low"
      );
      expect(last.querySelectorAll("[data-testid='attention-reasons'] li")).toHaveLength(1);
    });

    it("counts the flagged among the people not shown", async () => {
      overview.data = { tenants: crowd(120, 60) };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-not-shown")).toBeVisible();

      const shownFlagged = document.querySelectorAll(
        "[data-testid='people-wall'] [data-testid='attention-reasons']"
      ).length;
      const shown = allNameElements().length;
      await expect
        .element(page.getByTestId("wall-not-shown"))
        .toHaveTextContent(
          `${120 - shown} more people are not shown. ${60 - shownFlagged} of them are flagged.`
        );
    });
  });

  describe("small screens", () => {
    for (const [width, height] of [
      [390, 844],
      [844, 390],
    ]) {
      it(`keeps the footer on screen and counts every person at ${width} by ${height}`, async () => {
        await page.viewport(width, height);
        overview.data = { tenants: crowd(48, 6) };
        render(PeopleWall);
        await expect.element(page.getByTestId("wall-clock")).toBeVisible();
        await expect.element(page.getByTestId("wall-not-shown")).toBeVisible();

        for (const id of ["wall-connection", "wall-clock", "wall-not-shown"]) {
          const box = page.getByTestId(id).element().getBoundingClientRect();
          expect(box.top, id).toBeGreaterThanOrEqual(0);
          expect(box.bottom, id).toBeLessThanOrEqual(height);
          expect(box.right, id).toBeLessThanOrEqual(width);
        }
        const names = allNameElements();
        for (const name of names) {
          const box = name.getBoundingClientRect();
          expect(box.bottom, name.textContent ?? "").toBeLessThanOrEqual(height);
          expect(box.right, name.textContent ?? "").toBeLessThanOrEqual(width);
        }
        const text = page.getByTestId("wall-not-shown").element().textContent ?? "";
        const hidden = Number(/(\d+) more people are not shown\./.exec(text)?.[1]);
        expect(names.length + hidden).toBe(48);
        const root = wall().element();
        expect(root.scrollHeight).toBeLessThanOrEqual(root.clientHeight);
      });
    }
  });

  describe("tall flagged cards", () => {
    for (const [width, height] of [
      [390, 844],
      [844, 390],
    ]) {
      it(`keeps the footer on screen and counts every person with three reasons each at ${width} by ${height}`, async () => {
        await page.viewport(width, height);
        overview.data = {
          tenants: [...Array.from({ length: 6 }, (_, i) => flaggedMany(i)), ...Array.from({ length: 42 }, (_, i) => named(i + 6))],
        };
        render(PeopleWall);
        await expect.element(page.getByTestId("wall-clock")).toBeVisible();

        for (const id of ["wall-connection", "wall-clock"]) {
          const box = page.getByTestId(id).element().getBoundingClientRect();
          expect(box.bottom, id).toBeLessThanOrEqual(height);
          expect(box.right, id).toBeLessThanOrEqual(width);
        }
        await expect.poll(clippedCards).toBe(0);
        expect(page.getByTestId("wall-flagged").elements().length).toBeGreaterThanOrEqual(1);
        const text = page.getByTestId("wall-not-shown").elements()[0]?.textContent ?? "";
        const hidden = Number(/(\d+) more (?:people are|person is) not shown\./.exec(text)?.[1] ?? 0);
        expect(allNameElements().length + hidden, text).toBe(48);
      });
    }
  });

  describe("the top band's size", () => {
    for (const [width, height] of [
      [1280, 720],
      [1920, 1080],
    ]) {
      it(`holds all 6 flagged people from the first render, and after each refresh, at ${width} by ${height}`, async () => {
        await page.viewport(width, height);
        overview.data = { tenants: crowd(48, 6) };
        render(PeopleWall);
        await expect.element(page.getByTestId("wall-clock")).toBeVisible();
        await expect.poll(() => page.getByTestId("wall-flagged").elements().length).toBe(6);

        for (let refresh = 0; refresh < 3; refresh++) {
          overview.data = { tenants: overview.data!.tenants!.map((person) => ({ ...person })) };
          await new Promise((done) => setTimeout(done, 150));
          expect(page.getByTestId("wall-flagged").elements(), `refresh ${refresh}`).toHaveLength(6);
        }
      });
    }

    it("keeps the same people in the band across refreshes on a phone", async () => {
      await page.viewport(390, 844);
      overview.data = {
        tenants: [...Array.from({ length: 6 }, (_, i) => flaggedMany(i)), named(7)],
      };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-clock")).toBeVisible();
      await new Promise((done) => setTimeout(done, 150));
      const first = namesIn("wall-band");
      expect(first.length).toBeGreaterThanOrEqual(1);

      for (let refresh = 0; refresh < 3; refresh++) {
        overview.data = { tenants: overview.data!.tenants!.map((person) => ({ ...person })) };
        await new Promise((done) => setTimeout(done, 150));
        expect(namesIn("wall-band"), `refresh ${refresh}`).toEqual(first);
      }
    });
  });

  describe("a card that grows after the band was measured", () => {
    async function drawnFull() {
      overview.data = { tenants: crowd(48, 6) };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-clock")).toBeVisible();
      await expect.poll(() => page.getByTestId("wall-flagged").elements().length).toBe(6);
      await new Promise((done) => setTimeout(done, 300));
      expect(clippedCards()).toBe(0);
      return page.getByTestId("wall-flagged").first().element() as HTMLElement;
    }

    it("is never drawn clipped: the cards that no longer fit go to the columns", async () => {
      const first = await drawnFull();

      first.style.height = "200px";

      await expect.poll(clippedCards).toBe(0);
      const inBand = namesIn("wall-band");
      expect(inBand.length).toBeGreaterThanOrEqual(1);
      expect(inBand.length).toBeLessThan(6);
      expect([...inBand, ...namesIn("wall-columns")]).toEqual(
        expect.arrayContaining(Array.from({ length: 6 }, (_, i) => `Person 00${i}`))
      );
    });

    it("keeps the same people in the band across refreshes and when the card shrinks back", async () => {
      const first = await drawnFull();
      first.style.height = "200px";
      await expect.poll(() => page.getByTestId("wall-flagged").elements().length).toBeLessThan(6);
      await expect.poll(clippedCards).toBe(0);
      const cut = namesIn("wall-band");

      for (let refresh = 0; refresh < 3; refresh++) {
        overview.data = { tenants: overview.data!.tenants!.map((person) => ({ ...person })) };
        await new Promise((done) => setTimeout(done, 150));
        expect(namesIn("wall-band"), `refresh ${refresh}`).toEqual(cut);
      }

      first.style.height = "";
      await new Promise((done) => setTimeout(done, 150));
      expect(namesIn("wall-band")).toEqual(cut);
      expect(clippedCards()).toBe(0);
    });
  });

  describe("people not shown", () => {
    it("says this person is flagged when the one hidden person is flagged", async () => {
      await page.viewport(390, 844);
      let count = 6;
      for (; count < 60; count++) {
        overview.data = { tenants: crowd(count, count) };
        const view = render(PeopleWall);
        await expect.element(page.getByTestId("wall-clock")).toBeVisible();
        const text = page.getByTestId("wall-not-shown").elements()[0]?.textContent ?? "";
        view.unmount();
        if (text.includes("1 more person is not shown.")) {
          expect(text).toContain("1 more person is not shown. This person is flagged.");
          return;
        }
      }
      throw new Error("No crowd left exactly one person hidden");
    });
  });

  describe("type for distance", () => {
    function sizes() {
      const line = page.getByTestId("wall-line").first().element();
      const name = getComputedStyle(line.querySelector("p")!).fontSize;
      const value = getComputedStyle(
        line.querySelector("[data-slot='glucose-value-indicator'] > div")!
      ).fontSize;
      return {
        name: parseFloat(name),
        value: parseFloat(value),
        row: line.getBoundingClientRect().height,
      };
    }

    it("steps the type up with the screen, the value never smaller than the name, and fits 48 people at each", async () => {
      const seen: { name: number; value: number; row: number }[] = [];
      for (const [width, height] of [
        [1280, 720],
        [1920, 1080],
        [3840, 2160],
      ]) {
        await page.viewport(width, height);
        overview.data = { tenants: crowd(48, 6) };
        const view = render(PeopleWall);
        await expect.element(page.getByText("Person 047")).toBeVisible();

        const measured = sizes();
        expect(measured.value).toBeGreaterThanOrEqual(measured.name);
        expect(measured.row).toBe(wallStep(width).rowPx);
        expect(allNameElements()).toHaveLength(48);
        expect(page.getByTestId("wall-not-shown").elements()).toHaveLength(0);
        seen.push(measured);
        view.unmount();
      }

      expect(seen[1].name).toBeGreaterThan(seen[0].name);
      expect(seen[2].name).toBeGreaterThan(seen[1].name);
      expect(seen[1].value).toBeGreaterThan(seen[0].value);
      expect(seen[2].value).toBeGreaterThan(seen[1].value);
    });
  });

  describe("capacity", () => {
    it("fits 48 people, 6 of them flagged, at 1280 by 720 with no scrolling", async () => {
      overview.data = { tenants: crowd(48, 6) };
      render(PeopleWall);
      await expect.element(page.getByText("Person 047")).toBeVisible();

      const root = wall().element();
      expect(root.scrollHeight).toBeLessThanOrEqual(root.clientHeight);
      expect(root.scrollWidth).toBeLessThanOrEqual(root.clientWidth);
      expect(document.documentElement.scrollHeight).toBeLessThanOrEqual(window.innerHeight);

      const names = allNameElements();
      expect(names).toHaveLength(48);
      for (const name of names) {
        const box = name.getBoundingClientRect();
        expect(box.top, name.textContent ?? "").toBeGreaterThanOrEqual(0);
        expect(box.bottom, name.textContent ?? "").toBeLessThanOrEqual(720);
        expect(box.right, name.textContent ?? "").toBeLessThanOrEqual(1280);
      }
      expect(page.getByTestId("wall-not-shown").elements()).toHaveLength(0);
    });

    it("truncates a long name with an ellipsis rather than widening the line", async () => {
      overview.data = {
        tenants: [named(1, { displayName: "A very long name ".repeat(12) }), named(2)],
      };
      render(PeopleWall);
      await expect.element(page.getByText("Person 002")).toBeVisible();

      const long = page.getByTestId("wall-line").first().element().querySelector("p")!;
      expect(getComputedStyle(long).textOverflow).toBe("ellipsis");
      expect(long.scrollWidth).toBeGreaterThan(long.clientWidth);
    });

    it("states how many people are not shown, and the number is true", async () => {
      overview.data = { tenants: crowd(120, 6) };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-not-shown")).toBeVisible();

      const shown = allNameElements().length;
      expect(shown).toBeLessThan(120);
      await expect
        .element(page.getByTestId("wall-not-shown"))
        .toHaveTextContent(`${120 - shown} more people are not shown.`);
      const root = wall().element();
      expect(root.scrollHeight).toBeLessThanOrEqual(root.clientHeight);
    });

    it("lists the people in API order up to the cut", async () => {
      overview.data = { tenants: crowd(120, 6) };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-not-shown")).toBeVisible();

      const shown = [...namesIn("wall-band"), ...namesIn("wall-columns")];
      expect(shown).toEqual(
        Array.from({ length: shown.length }, (_, i) => `Person ${String(i).padStart(3, "0")}`)
      );
    });
  });
});

const birch: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000001", name: "Birch cabin", teamName: "Camp Wren", sharesName: false };
const oak: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000002", name: "Oak cabin", teamName: "Camp Wren", sharesName: false };
const pine: StaffGroup = { id: "0193b6a0-0000-7000-8000-000000000003", name: "Pine cabin", teamName: "Camp Wren", sharesName: false };

function notFound() {
  try {
    error(404, "Not found");
  } catch (thrown) {
    return thrown;
  }
}

/** Each column's rows, top to bottom: a heading's text in brackets, or a person's name. */
function columnRows() {
  return [...page.getByTestId("wall-columns").element().querySelectorAll(":scope > ul")].map((list) =>
    [...list.children].map((row) =>
      row.getAttribute("data-testid") === "wall-heading"
        ? `[${row.textContent?.replace(/\s+/g, " ").trim()}]`
        : row.querySelector("p[title]")?.textContent?.trim()
    )
  );
}

function notShownCount() {
  const text = page.getByTestId("wall-not-shown").elements()[0]?.textContent ?? "";
  return Number(text.match(/(\d+) more (?:people are|person is) not shown\./)?.[1] ?? 0);
}

describe("a group's wall", () => {
  it("opens on the group its address names, with the group's name, its people and how many it cannot see", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = [birch, oak];
    overview.data = { tenants: [named(9)] };
    overview.groupReads[birch.id!] = { tenants: [flagged(1), named(2), named(3)], hiddenCount: 2 };
    render(PeopleWall);
    await expect.element(page.getByText("Person 003")).toBeVisible();

    expect(refreshes.reads).toContainEqual({ group: birch.id, rules: OverviewRuleSet.Team });
    expect(
      refreshes.reads.filter(
        (read) => JSON.stringify(read) !== JSON.stringify({ group: birch.id, rules: OverviewRuleSet.Team })
      )
    ).toEqual([]);
    expect(namesIn("wall-band")).toEqual(["Person 001"]);
    expect(columnRows()).toEqual([["Person 002"], ["Person 003"]]);
    expect(page.getByText("Person 009").elements()).toHaveLength(0);
    await expect.element(page.getByTestId("wall-group")).toHaveTextContent("Birch cabin");
    await expect
      .element(page.getByTestId("wall-hidden-in-group"))
      .toHaveTextContent("You cannot see 2 people in this group.");
    await expect.element(page.getByTestId("wall-rules")).toHaveTextContent("Flagged by Nocturne's fixed rules.");
    expect(navigation.address.searchParams.get("group")).toBe(birch.id);
  });

  it("reads a group with its teams' rules and without the viewer's own, as the everyone wall does", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = [birch];
    overview.groupReads[birch.id!] = { tenants: [named(2)], hiddenCount: 0 };
    render(PeopleWall);
    await expect.element(page.getByText("Person 002")).toBeVisible();

    expect(refreshes.reads.length).toBeGreaterThan(0);
    expect(refreshes.reads).toEqual(refreshes.reads.map(() => ({ group: birch.id, rules: OverviewRuleSet.Team })));
  });

  it("lists the same people whatever else the address holds", async () => {
    openAt(`?group=${birch.id}&lens=review&days=30&person=p003`);
    overview.groups = [birch];
    overview.groupReads[birch.id!] = { tenants: [named(2), named(3)], hiddenCount: 0 };
    render(PeopleWall);
    await expect.element(page.getByText("Person 003")).toBeVisible();

    expect(refreshes.reads).toContainEqual({ group: birch.id, rules: OverviewRuleSet.Team });
    expect(
      refreshes.reads.filter(
        (read) => JSON.stringify(read) !== JSON.stringify({ group: birch.id, rules: OverviewRuleSet.Team })
      )
    ).toEqual([]);
    expect(columnRows().flat()).toEqual(["Person 002", "Person 003"]);
    expect(page.getByTestId("wall-hidden-in-group").elements()).toHaveLength(0);
  });

  it("says so when the viewer can see nobody in the group", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = [birch];
    overview.groupReads[birch.id!] = { tenants: [], hiddenCount: 1 };
    render(PeopleWall);

    await expect.element(page.getByText("You cannot see anyone in this group.")).toBeVisible();
    await expect
      .element(page.getByTestId("wall-hidden-in-group"))
      .toHaveTextContent("You cannot see 1 person in this group.");
  });

  it("shows everyone, says why, and drops the group from the address for a group the viewer is not in", async () => {
    openAt("?group=0193b6a0-0000-7000-8000-0000000000ff&other=1");
    overview.groups = [birch];
    overview.data = { tenants: [named(1)] };
    render(PeopleWall);

    await expect.element(page.getByText("Person 001")).toBeVisible();
    await expect
      .element(page.getByTestId("wall-fallback"))
      .toHaveTextContent("You are not in that group. The list shows everyone you follow.");
    expect(navigation.address.search).toBe("?other=1");
    expect(page.getByTestId("wall-group").elements()).toHaveLength(0);
  });

  it("shows everyone when the group's own read answers that the viewer is not in it", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = [birch];
    overview.groupErrors[birch.id!] = notFound();
    overview.data = { tenants: [named(1)] };
    render(PeopleWall);

    await expect.element(page.getByText("Person 001")).toBeVisible();
    await expect.element(page.getByTestId("wall-fallback")).toHaveTextContent(/^You are not in that group\./);
    expect(page.getByText("The list of people did not load.").elements()).toHaveLength(0);
    expect(navigation.address.searchParams.has("group")).toBe(false);
  });

  it("shows everyone when the viewer's groups did not load", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = undefined;
    overview.groupsError = new Error("The read failed.");
    overview.groupReads[birch.id!] = { tenants: [named(2)], hiddenCount: 0 };
    overview.data = { tenants: [named(1)] };
    render(PeopleWall);

    await expect.element(page.getByText("Person 001")).toBeVisible();
    await expect
      .element(page.getByTestId("wall-fallback"))
      .toHaveTextContent("Your groups did not load. The list shows everyone you follow.");
    expect(navigation.address.searchParams.has("group")).toBe(false);
  });

  it("reads its tab from the address a shallow rewrite shows, as the People page does", async () => {
    openAt("");
    Object.assign(pageState, {
      shallow: { url: new URL(`http://localhost/people/wall?group=${birch.id}`) },
    });
    overview.groups = [birch];
    overview.groupReads[birch.id!] = { tenants: [named(2)], hiddenCount: 0 };
    overview.data = { tenants: [named(1)] };
    render(PeopleWall);

    await expect.element(page.getByText("Person 002")).toBeVisible();
    expect(page.getByText("Person 001").elements()).toHaveLength(0);
    await expect.element(page.getByTestId("wall-group")).toHaveTextContent("Birch cabin");
  });

  it("goes back to the group's tab on Escape", async () => {
    openAt(`?group=${birch.id}`);
    overview.groups = [birch];
    overview.groupReads[birch.id!] = { tenants: [named(2)], hiddenCount: 0 };
    render(PeopleWall);
    await expect.element(page.getByText("Person 002")).toBeVisible();

    await userEvent.keyboard("{Escape}");

    expect(navigation.goto).toHaveBeenCalledWith(`/people?group=${birch.id}`, { replaceState: true });
  });
});

describe("headings on the wall of everyone", () => {
  it("puts the unflagged people under the group the API names, in the groups' order, then the people in none", async () => {
    overview.groups = [birch, oak, pine];
    overview.data = {
      tenants: [
        flagged(1, { firstGroupId: oak.id }),
        named(2, { firstGroupId: oak.id }),
        named(3, { firstGroupId: birch.id }),
        named(4),
        named(5, { firstGroupId: birch.id }),
        named(6, { firstGroupId: oak.id }),
      ],
    };
    render(PeopleWall);
    await expect.element(page.getByText("Person 006")).toBeVisible();

    expect(namesIn("wall-band")).toEqual(["Person 001"]);
    expect(columnRows().flat()).toEqual([
      "[Birch cabin]",
      "Person 003",
      "Person 005",
      "[Oak cabin]",
      "Person 002",
      "Person 006",
      "[Not in your groups]",
      "Person 004",
    ]);
    await expect.element(page.getByTestId("wall-rules")).toHaveTextContent("Flagged by Nocturne's fixed rules.");
  });

  it("shows no headings when only one heading would have people under it", async () => {
    overview.groups = [birch, oak];
    for (const firstGroupId of [birch.id, undefined]) {
      overview.data = {
        tenants: [flagged(1, { firstGroupId: oak.id }), named(2, { firstGroupId }), named(3, { firstGroupId })],
      };
      const view = render(PeopleWall);
      await expect.element(page.getByText("Person 003")).toBeVisible();

      expect(page.getByTestId("wall-heading").elements()).toHaveLength(0);
      expect(columnRows().flat()).toEqual(["Person 002", "Person 003"]);
      view.unmount();
    }
  });

  it("keeps the flagged people past the band at the top of the columns, under no heading", async () => {
    overview.groups = [birch];
    overview.data = {
      tenants: [
        ...Array.from({ length: 8 }, (_, i) => flagged(i, { firstGroupId: birch.id })),
        named(8, { firstGroupId: birch.id }),
        named(9),
      ],
    };
    render(PeopleWall);
    await expect.element(page.getByText("Person 009")).toBeVisible();
    await expect.poll(() => page.getByTestId("wall-flagged").elements().length).toBe(6);

    expect(columnRows().flat()).toEqual([
      "Person 006",
      "Person 007",
      "[Birch cabin]",
      "Person 008",
      "[Not in your groups]",
      "Person 009",
    ]);
  });

  it("repeats a heading at the top of a column its people run on into", async () => {
    overview.groups = [birch, oak];
    overview.data = {
      tenants: [
        ...Array.from({ length: 30 }, (_, i) => named(i, { firstGroupId: birch.id })),
        ...Array.from({ length: 10 }, (_, i) => named(30 + i, { firstGroupId: oak.id })),
      ],
    };
    render(PeopleWall);
    await expect.element(page.getByText("Person 000")).toBeVisible();

    const columns = columnRows();
    expect(columns.length).toBeGreaterThan(1);
    for (const column of columns) expect(column[0]).toMatch(/^\[/);
    expect(new Set(columns.flat().filter((row) => !row?.startsWith("["))).size).toBe(40);
  });

  it("hides a repeated heading from assistive technology, so each group is announced once", async () => {
    overview.groups = [birch, oak];
    overview.data = {
      tenants: [
        ...Array.from({ length: 30 }, (_, i) => named(i, { firstGroupId: birch.id })),
        ...Array.from({ length: 10 }, (_, i) => named(30 + i, { firstGroupId: oak.id })),
      ],
    };
    render(PeopleWall);
    await expect.element(page.getByText("Person 000")).toBeVisible();

    const headings = page.getByTestId("wall-heading").elements();
    const announced = headings.filter((heading) => heading.getAttribute("aria-hidden") !== "true");
    const hidden = headings.filter((heading) => heading.getAttribute("aria-hidden") === "true");
    expect(announced.map((heading) => heading.textContent?.trim())).toEqual(["Birch cabin", "Oak cabin"]);
    expect(hidden.length).toBeGreaterThan(0);
    expect(new Set(hidden.map((heading) => heading.textContent?.trim()))).toEqual(new Set(["Birch cabin"]));
    expect(announced[0]).toBe(headings[0]);
  });

  it("draws no headings and lists the people flat when the columns hold fewer than two rows", async () => {
    await page.viewport(1280, 150);
    overview.groups = [birch, oak];
    overview.data = {
      tenants: Array.from({ length: 12 }, (_, i) => named(i, { firstGroupId: [birch.id, oak.id, undefined][i % 3] })),
    };
    render(PeopleWall);
    await expect.element(page.getByTestId("wall-columns")).toBeVisible();

    const columnsBox = page.getByTestId("wall-columns").element().getBoundingClientRect();
    expect(columnsBox.height).toBeLessThan(2 * wallStep(1280).rowPx);
    expect(columnsBox.height).toBeGreaterThanOrEqual(wallStep(1280).rowPx);
    expect(page.getByTestId("wall-heading").elements()).toHaveLength(0);
    const shownNames = allNameElements().length;
    expect(shownNames).toBeGreaterThan(0);
    expect(columnRows().every((column) => column.length === 1)).toBe(true);
    expect(shownNames + notShownCount()).toBe(12);
  });

  for (const [width, height] of [
    [1280, 720],
    [390, 844],
  ]) {
    it(`counts the people under every heading among those not shown, with no heading alone, at ${width} by ${height}`, async () => {
      await page.viewport(width, height);
      overview.groups = [birch, oak];
      const homes = [birch.id, oak.id, undefined];
      overview.data = {
        tenants: Array.from({ length: 120 }, (_, i) =>
          i < 6 ? flagged(i) : named(i, { firstGroupId: homes[i % 3] })
        ),
      };
      render(PeopleWall);
      await expect.element(page.getByTestId("wall-not-shown")).toBeVisible();

      const columns = columnRows();
      expect(page.getByTestId("wall-heading").elements().length).toBeGreaterThan(0);
      for (const column of columns) {
        expect(column.at(-1)).not.toMatch(/^\[/);
        column.forEach((row, index) => {
          if (row?.startsWith("[")) expect(column[index + 1]).not.toMatch(/^\[/);
        });
      }
      const names = allNameElements().map((name) => name.textContent?.trim());
      expect(new Set(names).size).toBe(names.length);
      expect(names.length + notShownCount()).toBe(120);
      const root = wall().element();
      expect(root.scrollHeight).toBeLessThanOrEqual(root.clientHeight);
    });
  }
});

describe("the overview through its generated schema", () => {
  it("reads the teams whose rules applied", async () => {
    const response = TenantOverviewResponseSchema.parse({
      tenants: [named(1, { tenantId: "0193b6a0-0000-7000-8000-0000000000a1" })],
      hiddenCount: null,
      peopleWithRulesPending: 0,
      ruleTeams: [{ id: "0193b6a0-0000-7000-8000-0000000000b1", name: "Ward 4" }],
    });
    expect(response.ruleTeams).toEqual([{ id: "0193b6a0-0000-7000-8000-0000000000b1", name: "Ward 4" }]);

    overview.data = response;
    render(PeopleWall);

    await expect
      .element(page.getByTestId("wall-rules"))
      .toHaveTextContent("Flagged by Nocturne's fixed rules and Ward 4's rules.");
  });

  it("reads a person in none of the viewer's groups as under the last heading", async () => {
    const response = TenantOverviewResponseSchema.parse({
      tenants: [
        named(1, { tenantId: "0193b6a0-0000-7000-8000-0000000000a1", firstGroupId: null as unknown as undefined }),
        named(2, { tenantId: "0193b6a0-0000-7000-8000-0000000000a2", firstGroupId: birch.id }),
      ],
      hiddenCount: null,
    });
    expect(response.tenants?.[0].firstGroupId).toBeUndefined();
    expect(response.tenants?.[1].firstGroupId).toBe(birch.id);

    overview.groups = [birch];
    overview.data = response;
    render(PeopleWall);
    await expect.element(page.getByText("Person 002")).toBeVisible();

    expect(columnRows().flat()).toEqual(["[Birch cabin]", "Person 002", "[Not in your groups]", "Person 001"]);
  });
});
