import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";

const state = vi.hoisted(() => ({
  url: new URL("https://acme.nocturne.example/auth/confirm-email?code=link-code"),
  isAuthenticated: false,
  redeem: vi.fn(),
}));

const navigation = vi.hoisted(() => ({
  replaceState: vi.fn((_url: string, _state: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    get data() {
      return { isAuthenticated: state.isAuthenticated };
    },
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  ...navigation,
}));
vi.mock("#lib/api/generated/emailConfirmations.generated.remote.js", () => ({
  redeem: (request: { code: string }) => state.redeem(request),
}));

import ConfirmEmailPage from "./+page.svelte";

describe("Confirm email page", () => {
  beforeEach(() => {
    state.url = new URL("https://acme.nocturne.example/auth/confirm-email?code=link-code");
    state.isAuthenticated = false;
    state.redeem.mockReset();
    state.redeem.mockResolvedValue(undefined);
    navigation.replaceState.mockClear();
  });

  it("redeems the code, drops it from the address bar and confirms", async () => {
    render(ConfirmEmailPage);

    await expect.element(page.getByText("Your email address is confirmed")).toBeInTheDocument();
    expect(state.redeem).toHaveBeenCalledWith({ code: "link-code" });
    expect(navigation.replaceState).toHaveBeenCalled();
    expect(navigation.replaceState.mock.calls[0][0]).not.toContain("link-code");
    await expect.element(page.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/auth/login");
  });

  it("leads a signed-in visitor to the app", async () => {
    state.isAuthenticated = true;
    render(ConfirmEmailPage);

    await expect.element(page.getByRole("link", { name: "Open Nocturne" })).toHaveAttribute("href", "/");
  });

  it("says only that the link expired or was used when it is refused", async () => {
    state.redeem.mockImplementation(async () => error(400, "Bad Request"));
    render(ConfirmEmailPage);

    await expect
      .element(page.getByText("The link has expired or was already used."))
      .toBeInTheDocument();
    await expect.element(page.getByText("Your email address is confirmed")).not.toBeInTheDocument();
  });

  it("refuses a visit with no code without calling the API", async () => {
    state.url = new URL("https://acme.nocturne.example/auth/confirm-email");
    render(ConfirmEmailPage);

    await expect
      .element(page.getByText("The link has expired or was already used."))
      .toBeInTheDocument();
    expect(state.redeem).not.toHaveBeenCalled();
  });
});
