import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  GlucoseStatus,
  NotificationUrgency,
  TenantOverviewAttentionKind,
  type TenantOverviewItem,
} from "@nocturne/api-client";
import { bg, bgLabel, time } from "#lib/utils/formatting.js";
import { wallStep } from "./wall-layout.js";
import { OVERLAY_REVEAL_MS } from "#lib/utils/overlay-reveal.svelte.js";
import { OVERVIEW_PING_DEBOUNCE_MS } from "./live-overview.svelte.js";
import { makePerson } from "./people-fixtures.js";
import { overviewFeed as feed, overviewSockets as clients } from "./people-socket-fixture.svelte.js";

const overview = $state<{
  data: { tenants: TenantOverviewItem[] } | undefined;
  error: unknown;
}>({ data: undefined, error: undefined });
const refreshes = vi.hoisted(() => ({ count: 0, run: async () => {} }));
const navigation = vi.hoisted(() => ({
  goto: vi.fn(async () => {}),
  afterNavigate: (callback: (navigation: unknown) => void) => {
    navigation.arrived = callback;
  },
  arrived: (_navigation: unknown) => {},
}));

vi.mock("$app/navigation", () => navigation);
vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({ pollWhileVisible: () => {} }));
vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));
vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getOverview: () => ({
    get current() {
      return overview.data;
    },
    get error() {
      return overview.error;
    },
    loading: false,
    refresh: async () => {
      refreshes.count++;
      await refreshes.run();
    },
  }),
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

beforeEach(async () => {
  await page.viewport(1280, 720);
  overview.data = undefined;
  overview.error = undefined;
  feed.connected = false;
  refreshes.count = 0;
  refreshes.run = async () => {};
  clients.length = 0;
  navigation.goto.mockClear();
  navigation.arrived = () => {};
});

afterEach(() => {
  vi.useRealTimers();
});

describe("People wall display", () => {
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
