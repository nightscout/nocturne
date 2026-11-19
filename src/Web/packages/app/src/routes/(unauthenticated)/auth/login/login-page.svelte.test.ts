import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("http://sam.localhost/auth/login"),
  isAuthenticated: false,
  unreadable: false,
  goto: vi.fn(async (_url: string, _options?: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    data: { tenantless: false },
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: state.goto,
}));
vi.mock("../auth.remote", () => ({
  getAuthState: () =>
    remoteQuery(() =>
      state.isAuthenticated
        ? { isAuthenticated: true, unreadable: false, user: { subjectId: "s" } }
        : { isAuthenticated: false, unreadable: state.unreadable, user: null },
    ),
}));
vi.mock("#lib/api/generated/index.js", () => ({
  getAuthStatus: () => remoteQuery(() => ({ allowAccessRequests: true })),
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  accessRequestOptions: vi.fn(),
  accessRequestComplete: vi.fn(),
}));
vi.mock("#lib/components/auth/LoginForm.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/auth/GuestCodeForm.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("water-ui", async (importOriginal) => ({
  ...(await importOriginal<typeof import("water-ui")>()),
  Artwork: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import LoginPage from "./+page.svelte";

function loginWith(returnUrl: string) {
  state.url = new URL(`http://sam.localhost/auth/login?${new URLSearchParams({ returnUrl })}`);
  return render(LoginPage, { data: {} } as never);
}

const DRAFT_KEY = "nocturne.member.custom-report-draft.dev.s.new";

beforeEach(() => {
  state.isAuthenticated = false;
  state.unreadable = false;
  localStorage.clear();
  state.goto.mockClear();
});

describe("Login page", () => {
  it("opens the membership request dialog in place, without leaving for sign-in", async () => {
    loginWith("/calendar");

    await page.getByTestId("request-membership-link").click();

    await expect.element(page.getByTestId("request-membership-dialog")).toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Send request" })).toBeInTheDocument();
    expect(state.goto).not.toHaveBeenCalled();
  });

  it("sends a visitor who is already signed in home rather than off-site", async () => {
    state.isAuthenticated = true;

    loginWith("https://evil.test/x");

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalledWith("/", { replace: true }));
  });

  it("clears what the browser kept for a member once the server says nobody is signed in", async () => {
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await vi.waitFor(() => expect(localStorage.getItem(DRAFT_KEY)).toBeNull());
  });

  it("keeps the drafts of a member who is still signed in", async () => {
    state.isAuthenticated = true;
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalled());
    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });

  it("keeps the drafts when the server could not read the session", async () => {
    state.unreadable = true;
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await expect.element(page.getByTestId("sign-in-card")).toBeInTheDocument();
    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });
});
