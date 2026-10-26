import { render } from "vitest-browser-svelte";
import type { ComponentProps } from "svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

let expiresAt: string | undefined;

const navigation = vi.hoisted(() => ({
  invalidate: vi.fn(async (_key: string) => {}),
  afterNavigate: vi.fn((_callback: () => void) => {}),
}));

const shell = vi.hoisted(() => ({ probe: vi.fn() }));

vi.mock("#lib/api/shell-session.remote.js", () => ({
  getShellSession: () => createQueryResource(shell.probe),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  ...navigation,
}));

vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
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
import NavigationProbe, { navigationProbe } from "./NavigationProbe.test.svelte";
import type { ShellSession } from "#lib/utils/shell-session.js";
import { AuthStore } from "#lib/stores/auth-store.svelte.js";
import { RealtimeStore } from "#lib/stores/realtime-store.svelte.js";
import ToolsLayout from "./tools/+layout.svelte";
import { EngineHost } from "water-ui";
import { illustrations } from "#lib/stores/illustrations.svelte.js";
import * as appState from "$app/state";
import { SHELL_DATA_KEY, SHELL_REVALIDATE_INTERVAL_MS } from "#lib/constants/shell-data.js";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

const BASE = new Date("2026-09-21T12:00:00Z").getTime();

// A tenantless host keeps the tenant-scoped surfaces (alerts, membership
// requests, the backup sign-in prompt) unmounted, leaving only the shell.
type LayoutData = ComponentProps<typeof AuthenticatedLayoutHarness>["data"];

function layoutData(overrides: Partial<LayoutData> = {}): LayoutData {
  return {
    tenantless: true,
    isAuthenticated: true,
    hasAccount: true,
    isShareHost: false,
    canViewRealtimeData: false,
    showBackupSignInPrompt: false,
    tenantTargets: [],
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
    pillRows: { desktop: 1, mobile: 1 },
    displayPreferences: [],
    displayLanguage: "en",
    serverPreferences: null,
    tenantPreferences: null,
    user: {
      subjectId: "subject-1",
      name: "Test User",
      roles: [],
      permissions: [],
    },
    ...overrides,
  };
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
}

function changeVisibility(hidden: boolean) {
  setHidden(hidden);
  document.dispatchEvent(new Event("visibilitychange"));
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
    setHidden(false);
    navigation.invalidate.mockClear();
    navigation.afterNavigate.mockClear();
    shell.probe.mockReset();
    vi.restoreAllMocks();
  });

  it("shows a guest session its own countdown", async () => {
    render(AuthenticatedLayoutHarness, {
      props: {
        data: layoutData({
          isGuestSession: true,
          guestExpiresAt: new Date(BASE + 2 * 60 * 1000).toISOString(),
        }),
      },
    });

    await expect.element(page.getByText("Guest access")).toBeVisible();
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

  describe("revalidating the shell data", () => {
    const sameSession: ShellSession = {
      subjectId: "subject-1",
      scopes: [],
      limitTo24Hours: false,
      isGuestSession: false,
      guestExpiresAt: null,
      shareLinkLive: true,
    };

    const shellInvalidations = () =>
      navigation.invalidate.mock.calls.filter(([key]) => key === SHELL_DATA_KEY).length;

    async function mountWith(data: Partial<LayoutData> = {}, section?: ComponentProps<typeof AuthenticatedLayoutHarness>["section"]) {
      shell.probe.mockResolvedValue(sameSession);
      render(AuthenticatedLayoutHarness, { props: { data: layoutData(data), section } });
      await expect.element(page.getByText("page content")).toBeVisible();
    }

    async function returnToTab() {
      changeVisibility(true);
      changeVisibility(false);
      await vi.waitFor(() => expect(shell.probe).toHaveBeenCalled());
      await vi.advanceTimersByTimeAsync(0);
    }

    it("leaves the shell data alone when the session still matches", async () => {
      await mountWith();

      await returnToTab();

      expect(shell.probe).toHaveBeenCalledTimes(1);
      expect(shellInvalidations()).toBe(0);
    });

    it.each([
      { name: "a different user signed in", probe: { subjectId: "subject-2" } },
      { name: "the session is gone", probe: { subjectId: null } },
      { name: "the grants changed", probe: { scopes: ["glucose.read"] } },
      { name: "the history clamp changed", probe: { limitTo24Hours: true } },
      { name: "the share link was revoked", probe: { shareLinkLive: false }, data: { isShareHost: true } },
    ])("invalidates the shell key once when $name", async ({ probe, data }) => {
      await mountWith(data);
      shell.probe.mockResolvedValue({ ...sameSession, ...probe });

      await returnToTab();

      expect(shellInvalidations()).toBe(1);
    });

    it("invalidates when a guest link is revoked", async () => {
      const guestExpiresAt = new Date(BASE + 60 * 60 * 1000).toISOString();
      await mountWith({ user: null, isGuestSession: true, guestExpiresAt });
      shell.probe.mockResolvedValue({
        ...sameSession,
        subjectId: null,
        isGuestSession: false,
      });

      await returnToTab();

      expect(shellInvalidations()).toBe(1);
    });

    it("ignores a failed probe and asks again at the next trigger", async () => {
      await mountWith();
      shell.probe.mockRejectedValueOnce(new Error("502"));

      await returnToTab();
      expect(shellInvalidations()).toBe(0);

      shell.probe.mockResolvedValue({ ...sameSession, subjectId: "subject-2" });
      await returnToTab();

      expect(shellInvalidations()).toBe(1);
    });

    it("does not probe while offline", async () => {
      await mountWith();
      vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);

      changeVisibility(true);
      changeVisibility(false);
      await vi.advanceTimersByTimeAsync(0);

      expect(shell.probe).not.toHaveBeenCalled();
    });

    it("probes on the interval while the tab is visible", async () => {
      await mountWith();

      await vi.advanceTimersByTimeAsync(SHELL_REVALIDATE_INTERVAL_MS * 2);

      expect(shell.probe).toHaveBeenCalledTimes(2);
    });

    it("does not probe while the tab is hidden", async () => {
      await mountWith();

      changeVisibility(true);
      await vi.advanceTimersByTimeAsync(SHELL_REVALIDATE_INTERVAL_MS * 3);

      expect(shell.probe).not.toHaveBeenCalled();
    });

    it("stops probing once the layout is destroyed", async () => {
      shell.probe.mockResolvedValue(sameSession);
      const screen = render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });
      await expect.element(page.getByText("page content")).toBeVisible();

      screen.unmount();
      await vi.advanceTimersByTimeAsync(SHELL_REVALIDATE_INTERVAL_MS * 3);
      changeVisibility(true);
      changeVisibility(false);

      expect(shell.probe).not.toHaveBeenCalled();
    });

    it("does not probe on a client navigation", async () => {
      navigationProbe.refreshes = 0;
      await mountWith({}, NavigationProbe);

      for (const [callback] of navigation.afterNavigate.mock.calls) {
        callback();
        callback();
      }

      expect(navigationProbe.refreshes).toBeGreaterThan(0);
      expect(shell.probe).not.toHaveBeenCalled();
    });
  });

  it("reloads the auth store's session when a revalidation finds a different user", async () => {
    const screen = render(AuthenticatedLayoutHarness, { props: { data: layoutData() } });
    await expect.element(page.getByText("page content")).toBeVisible();
    const loadSession = vi.spyOn(AuthStore.prototype, "loadSession").mockResolvedValue();

    await screen.rerender({ data: layoutData() });
    expect(loadSession).not.toHaveBeenCalled();

    await screen.rerender({
      data: layoutData({
        user: { subjectId: "subject-2", name: "Other User", roles: [], permissions: [] },
      }),
    });

    await vi.waitFor(() => expect(loadSession).toHaveBeenCalledTimes(1));
  });

  it("tears realtime down when glucose read is withdrawn and restarts it when restored", async () => {
    const initialize = vi.spyOn(RealtimeStore.prototype, "initialize").mockResolvedValue();
    const destroy = vi.spyOn(RealtimeStore.prototype, "destroy").mockImplementation(() => {});
    const screen = render(AuthenticatedLayoutHarness, {
      props: { data: layoutData({ tenantless: false, canViewRealtimeData: true }) },
    });
    await expect.element(page.getByText("page content")).toBeVisible();
    expect(initialize).toHaveBeenCalled();
    expect(destroy).not.toHaveBeenCalled();

    await screen.rerender({ data: layoutData({ tenantless: false, canViewRealtimeData: false }) });

    await vi.waitFor(() => expect(destroy).toHaveBeenCalledTimes(1));

    initialize.mockClear();
    await screen.rerender({ data: layoutData({ tenantless: false, canViewRealtimeData: true }) });
    await vi.waitFor(() => expect(initialize).toHaveBeenCalledTimes(1));

    await screen.rerender({ data: layoutData({ tenantless: false, canViewRealtimeData: false }) });
    await vi.waitFor(() => expect(destroy).toHaveBeenCalledTimes(2));
  });
});
