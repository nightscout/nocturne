import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { flushSync } from "svelte";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";
import type { AuthStore } from "$lib/stores/auth-store.svelte";

let expiresAt: string | undefined;

vi.mock("$routes/(unauthenticated)/auth/auth.remote", () => ({
  getSessionInfo: () =>
    remoteQuery(() => ({
      isAuthenticated: true,
      subjectId: "subject-1",
      name: "Test User",
      roles: [],
      permissions: [],
      expiresAt,
    })),
  refreshSession: remoteCommand(async () => ({ success: true, expiresAt })),
  logoutSession: remoteCommand(async () => ({ success: true })),
}));

import SessionExpiryWatcherHarness from "./SessionExpiryWatcherHarness.test.svelte";

const BASE = new Date("2026-09-21T12:00:00Z").getTime();
const TICK_MS = 30_000;

const warning = () => page.getByText("Session Expiring");

function expiresIn(ms: number) {
  expiresAt = new Date(BASE + ms).toISOString();
}

/**
 * Renders the watcher over a real auth store and resolves once that store has
 * pulled the session in, so a test can move the clock against a known expiry
 * rather than against whenever the load happened to settle.
 */
async function renderLoaded(): Promise<AuthStore> {
  let captured: AuthStore | undefined;
  render(SessionExpiryWatcherHarness, {
    props: {
      tickMs: TICK_MS,
      onstore: (store: AuthStore) => (captured = store),
    },
  });
  flushSync();
  if (!captured) throw new Error("the harness did not hand back its store");
  await captured.loadSession();
  flushSync();
  return captured;
}

describe("SessionExpiryWatcher", () => {
  beforeEach(() => {
    document.cookie = "IsAuthenticated=true; path=/";
    expiresIn(6 * 60 * 60 * 1000);
    // Installed before the render, so the watcher's interval is scheduled on
    // the fake clock rather than the real one. `setTimeout` stays real: the
    // runner polls the DOM with it.
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    vi.setSystemTime(BASE);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("warns when the session is about to expire", async () => {
    expiresIn(2 * 60 * 1000);

    await renderLoaded();

    await expect.element(warning()).toBeVisible();
  });

  it("stays out of the way while the session has hours left", async () => {
    await renderLoaded();

    await expect.element(warning()).not.toBeInTheDocument();
  });

  it("raises the warning when the clock crosses into the window", async () => {
    // Outside the five-minute window at render, inside it once time passes and
    // nothing else changes.
    expiresIn(400 * 1000);

    await renderLoaded();

    await expect.element(warning()).not.toBeInTheDocument();

    vi.advanceTimersByTime(5 * TICK_MS);
    flushSync();

    await expect.element(warning()).toBeVisible();
  });

  it("stays dismissed across the next tick", async () => {
    expiresIn(2 * 60 * 1000);

    await renderLoaded();

    await expect.element(warning()).toBeVisible();

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.element(warning()).not.toBeInTheDocument();

    // Still inside the window afterwards, so only the dismissal keeps it away.
    vi.advanceTimersByTime(2 * TICK_MS);
    flushSync();

    await expect.element(warning()).not.toBeInTheDocument();
  });

  it("warns again once the session has been extended", async () => {
    expiresIn(2 * 60 * 1000);

    const store = await renderLoaded();

    await page.getByRole("button", { name: "Dismiss" }).click();
    await expect.element(warning()).not.toBeInTheDocument();

    expiresIn(3 * 60 * 1000);
    await store.loadSession();
    flushSync();

    await expect.element(warning()).toBeVisible();
  });
});
