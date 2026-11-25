import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

type Refresh = { success: true; expiresAt: string } | { success: false; refused: boolean };
type Session = { isAuthenticated: boolean; unavailable: boolean; subjectId?: string; expiresAt?: string };

const remote = vi.hoisted(() => ({
  refresh: null as unknown as () => unknown,
  session: null as unknown as () => unknown,
}));

vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
  getSessionInfo: () => remoteQuery(() => remote.session()),
  refreshSession: remoteCommand(async () => remote.refresh()),
  logoutSession: remoteCommand(async () => ({ success: true })),
}));

import { AuthStore } from "./auth-store.svelte.js";

const DRAFT_KEY = "nocturne.member.custom-report-draft.dev.subject-1.new";
const LATER = "2099-01-01T00:00:00Z";

let store: AuthStore;

function signedIn(): AuthStore {
  remote.session = (): Session => ({ isAuthenticated: true, unavailable: false, subjectId: "subject-1", expiresAt: LATER });
  return new AuthStore();
}

beforeEach(async () => {
  document.cookie = "IsAuthenticated=true; path=/";
  store = signedIn();
  await store.loadSession();
  expect(store.isAuthenticated).toBe(true);
  localStorage.setItem(DRAFT_KEY, "{}");
});

afterEach(() => {
  store.destroy();
  document.cookie = "IsAuthenticated=; path=/; max-age=0";
  localStorage.clear();
});

describe("a session refresh that fails", () => {
  it("keeps the drafts and the session when the API does not answer", async () => {
    remote.refresh = (): Refresh => ({ success: false, refused: false });

    expect(await store.refreshSession()).toBe(false);

    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
    expect(store.isAuthenticated).toBe(true);
  });

  it("keeps the drafts when the refresh was refused but the session is still there", async () => {
    remote.refresh = (): Refresh => ({ success: false, refused: true });

    expect(await store.refreshSession()).toBe(true);

    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
    expect(store.isAuthenticated).toBe(true);
  });

  it("keeps the drafts when the refresh was refused and the session check fails", async () => {
    remote.refresh = (): Refresh => ({ success: false, refused: true });
    remote.session = (): Session => ({ isAuthenticated: false, unavailable: true });

    expect(await store.refreshSession()).toBe(false);

    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });

  it("clears the drafts when the refresh was refused and the API says the session is gone", async () => {
    remote.refresh = (): Refresh => ({ success: false, refused: true });
    remote.session = (): Session => ({ isAuthenticated: false, unavailable: false });

    expect(await store.refreshSession()).toBe(false);

    expect(localStorage.getItem(DRAFT_KEY)).toBeNull();
    expect(store.isAuthenticated).toBe(false);
  });
});

describe("loading the session", () => {
  it("keeps the drafts when the API does not answer", async () => {
    remote.session = (): Session => ({ isAuthenticated: false, unavailable: true });

    await store.loadSession();

    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });

  it("clears the drafts when the API says nobody is signed in", async () => {
    remote.session = (): Session => ({ isAuthenticated: false, unavailable: false });

    await store.loadSession();

    expect(localStorage.getItem(DRAFT_KEY)).toBeNull();
  });
});
