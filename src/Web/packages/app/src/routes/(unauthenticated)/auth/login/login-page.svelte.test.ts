import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("http://sam.localhost/auth/login"),
  isAuthenticated: false,
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
        ? { isAuthenticated: true, user: { subjectId: "s" } }
        : { isAuthenticated: false, user: null },
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

beforeEach(() => {
  state.isAuthenticated = false;
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
});
