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
    document.cookie = "IsAuthenticated=true; path=/";
    expiresAt = new Date(BASE + 2 * 60 * 1000).toISOString();
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    vi.setSystemTime(BASE);
  });

  afterEach(() => {
    vi.useRealTimers();
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
});
