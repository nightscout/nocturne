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
  sessionStorage.clear();
  state.isAuthenticated = false;
  state.goto.mockClear();
});

describe("Login page", () => {
  it("sends a membership request to sign-in with the page's own destination and a flow id", async () => {
    loginWith("/calendar");

    await page.getByTestId("request-membership-link").click();
    await page.getByRole("textbox").fill("hello");
    await page.getByRole("button", { name: "Continue to sign up" }).click();

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalledOnce());
    const target = new URL(String(state.goto.mock.calls[0][0]), "http://sam.localhost");
    expect(target.searchParams.get("returnUrl")).toMatch(/^\/calendar\?membershipRequest=[0-9a-f-]{36}$/);
  });

  it("refuses a destination that is not a same-origin path", async () => {
    for (const returnUrl of ["https://evil.test/x", "//evil.test/x", "javascript:alert(1)"]) {
      state.goto.mockClear();
      const login = loginWith(returnUrl);

      await page.getByTestId("request-membership-link").click();
      await page.getByRole("textbox").fill("hello");
      await page.getByRole("button", { name: "Continue to sign up" }).click();

      await vi.waitFor(() => expect(state.goto).toHaveBeenCalledOnce());
      const target = new URL(String(state.goto.mock.calls[0][0]), "http://sam.localhost");
      expect(target.searchParams.get("returnUrl")).toMatch(/^\/\?membershipRequest=/);
      login.unmount();
    }
  });

  it("sends a visitor who is already signed in home rather than off-site", async () => {
    state.isAuthenticated = true;

    loginWith("https://evil.test/x");

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalledWith("/", { replace: true }));
  });
});
