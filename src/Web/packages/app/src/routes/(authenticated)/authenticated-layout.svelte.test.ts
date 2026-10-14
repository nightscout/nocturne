import { render } from "vitest-browser-svelte";
import type { ComponentProps } from "svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";

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

import AuthenticatedLayoutHarness from "./AuthenticatedLayoutHarness.test.svelte";
import SettingsLayout from "./settings/+layout.svelte";
import InitOrderProbe, { probe } from "./InitOrderProbe.test.svelte";
import { RealtimeStore } from "$lib/stores/realtime-store.svelte";
import ToolsLayout from "./tools/+layout.svelte";
import { EngineHost } from "@nocturne/watercolour";
import { illustrations } from "$lib/stores/illustrations.svelte";
import * as appState from "$app/state";

const BASE = new Date("2026-09-21T12:00:00Z").getTime();

const warning = () => page.getByText("Session Expiring");

// A tenantless host keeps the tenant-scoped surfaces (alerts, membership
// requests, the backup sign-in prompt) unmounted, leaving the shell and the
// session-expiry watcher, which is not tenant-scoped.
type LayoutData = ComponentProps<typeof AuthenticatedLayoutHarness>["data"];

function layoutData(overrides: Partial<LayoutData> = {}): LayoutData {
  return {
    tenantless: true,
    isAuthenticated: true,
    isShareHost: false,
    canViewRealtimeData: false,
    isDemo: false,
    isGuestSession: false,
    isPlatformAdmin: false,
    isPlatformAccessGrant: false,
    tenantSlug: null,
    baseDomain: null,
    guestExpiresAt: null,
    nextResetAt: null,
    lastSignIn: null,
    dashboardSlugs: [],
    effectivePermissions: [],
    limitTo24Hours: false,
    refusedAsDemoSubject: false,
    onboardingComplete: true,
    displayPreferences: [],
    displayLanguage: "en",
    serverPreferences: null,
    user: {
      subjectId: "subject-1",
      name: "Test User",
      roles: [],
      permissions: [],
    },
    ...overrides,
  };
}

describe("authenticated layout", () => {
  beforeEach(() => {
    vi.spyOn(EngineHost.prototype, "warm").mockResolvedValue(true);
    document.cookie = "IsAuthenticated=true; path=/";
    expiresAt = new Date(BASE + 2 * 60 * 1000).toISOString();
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    vi.setSystemTime(BASE);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("mounts the session expiry warning for a signed-in user", async () => {
    render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });

    await expect.element(warning()).toBeVisible();
  });

  it("leaves a guest session to its own countdown", async () => {
    render(AuthenticatedLayoutHarness, {
      props: {
        data: layoutData({
          isGuestSession: true,
          guestExpiresAt: new Date(BASE + 2 * 60 * 1000).toISOString(),
        }),
      },
    });

    await expect.element(page.getByText("Guest access")).toBeVisible();
    await expect.element(warning()).not.toBeInTheDocument();
  });

  it.each([
    { name: "a top-level page", section: undefined },
    { name: "a settings page", section: SettingsLayout },
    { name: "a tools page", section: ToolsLayout },
  ])("gives $name exactly one main landmark", async ({ section }) => {
    render(AuthenticatedLayoutHarness, {
      props: { data: layoutData(), section },
    });

    await expect
      .element(page.getByRole("main").getByText("page content"))
      .toBeVisible();
    expect(page.getByRole("main").elements()).toHaveLength(1);
  });

  it("loads the command palette on its first toggle", async () => {
    render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });
    await expect.element(page.getByText("page content")).toBeVisible();

    const search = page.getByPlaceholder("Search commands...");
    expect(search.elements()).toHaveLength(0);

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }));

    await expect.element(search).toBeVisible();

    // Closed before teardown: left open, its command list throws once the next test mounts.
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }));
    await expect.element(search).not.toBeInTheDocument();
  });

  it("loads the join welcome only for an arrival carrying its marker", async () => {
    appState.page.url = new URL("http://localhost/") as typeof appState.page.url;
    const { unmount } = render(AuthenticatedLayoutHarness, { props: { data: layoutData({ tenantless: false }) } });
    await expect.element(page.getByText("page content")).toBeVisible();
    expect(page.getByTestId("join-welcome").elements()).toHaveLength(0);
    unmount();

    appState.page.url = new URL("http://localhost/?welcome=1") as typeof appState.page.url;
    Object.assign(appState.page.data, { effectivePermissions: ["glucose.read"] });
    render(AuthenticatedLayoutHarness, { props: { data: layoutData({ tenantless: false }) } });

    await expect.element(page.getByTestId("join-welcome")).toBeVisible();
    appState.page.url = new URL("http://localhost") as typeof appState.page.url;
    delete appState.page.data.effectivePermissions;
  });

  it("starts the realtime store before the page beneath it initialises", async () => {
    probe.initializeCalls = 0;
    probe.seenAtInit = -1;
    const initialize = vi
      .spyOn(RealtimeStore.prototype, "initialize")
      .mockImplementation(async () => {
        probe.initializeCalls++;
      });

    render(AuthenticatedLayoutHarness, {
      props: {
        data: layoutData({ tenantless: false, canViewRealtimeData: true }),
        section: InitOrderProbe,
      },
    });

    await expect.element(page.getByText("page content")).toBeVisible();
    expect(probe.seenAtInit).toBe(1);
    initialize.mockRestore();
  });

  describe("engine warm-up", () => {
    const warm = () => vi.mocked(EngineHost.prototype.warm);

    beforeEach(() => {
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
      vi.setSystemTime(BASE);
      vi.spyOn(performance, "now").mockImplementation(() => Date.now() - BASE);
    });

    afterEach(() => {
      illustrations.current = "animated";
    });

    it("holds the warm-up off for five seconds from navigation", async () => {
      render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });

      await vi.advanceTimersByTimeAsync(4900);
      expect(warm()).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(100);
      await vi.waitFor(() => expect(warm()).toHaveBeenCalledOnce());
    });

    it("waits for the current reading when the page has one", async () => {
      const initialize = vi.spyOn(RealtimeStore.prototype, "initialize").mockResolvedValue();

      render(AuthenticatedLayoutHarness, {
        props: { data: layoutData({ tenantless: false, canViewRealtimeData: true }) },
      });

      await vi.advanceTimersByTimeAsync(20_000);
      expect(warm()).not.toHaveBeenCalled();

      (initialize.mock.contexts[0] as RealtimeStore).currentReadingLoaded = true;
      await vi.advanceTimersByTimeAsync(0);
      await vi.waitFor(() => expect(warm()).toHaveBeenCalledOnce());
    });

    it("does not warm with illustrations off", async () => {
      illustrations.current = "off";
      render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });

      await vi.advanceTimersByTimeAsync(20_000);
      expect(warm()).not.toHaveBeenCalled();
    });
  });
});
