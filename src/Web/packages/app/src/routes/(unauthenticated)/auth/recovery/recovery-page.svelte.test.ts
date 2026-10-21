import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";

const state = vi.hoisted(() => ({
  url: new URL("https://acme.nocturne.example/auth/recovery"),
  redeem: vi.fn(),
}));

const navigation = vi.hoisted(() => ({
  goto: vi.fn(async (_url: string, _options?: unknown) => {}),
  replaceState: vi.fn((_url: string, _state: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    data: { isAuthenticated: false },
  },
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  ...navigation,
}));

vi.mock("$lib/api/generated/passkeys.generated.remote", () => ({
  recoveryModeRedeem: (request: { code: string }) => state.redeem(request),
}));

import RecoveryPage from "./+page.svelte";

describe("Recovery mode page", () => {
  beforeEach(() => {
    state.url = new URL("https://acme.nocturne.example/auth/recovery");
    state.redeem.mockReset();
    state.redeem.mockResolvedValue({ username: "alex" });
    navigation.goto.mockClear();
    navigation.replaceState.mockClear();
  });

  it("spends a typed code and moves on to registering a passkey for its account", async () => {
    render(RecoveryPage);

    await page.getByLabelText("Recovery code from your administrator").fill("  typed-code  ");
    await page.getByRole("button", { name: "Continue" }).click();

    await vi.waitFor(() => expect(navigation.goto).toHaveBeenCalled());
    expect(state.redeem).toHaveBeenCalledWith({ code: "typed-code" });
    expect(navigation.goto.mock.calls[0][0]).toContain("/auth/recovery/passkey?username=alex");
  });

  it("redeems the code a link carries straight away and drops it from the address bar", async () => {
    state.url = new URL("https://acme.nocturne.example/auth/recovery?code=linked-code");

    render(RecoveryPage);

    await vi.waitFor(() => expect(navigation.goto).toHaveBeenCalled());
    expect(state.redeem).toHaveBeenCalledWith({ code: "linked-code" });
    expect(navigation.replaceState).toHaveBeenCalled();
  });

  it("explains a code that no longer works", async () => {
    state.redeem.mockImplementation(async () => error(400, "Bad Request"));
    render(RecoveryPage);

    await page.getByLabelText("Recovery code from your administrator").fill("spent-code");
    await page.getByRole("button", { name: "Continue" }).click();

    await expect
      .element(page.getByText("That recovery code has expired or was already used.", { exact: false }))
      .toBeInTheDocument();
    expect(navigation.goto).not.toHaveBeenCalled();
  });

  it("reports a throttled attempt as throttled", async () => {
    state.redeem.mockImplementation(async () => error(429, "Too many requests"));
    render(RecoveryPage);

    await page.getByLabelText("Recovery code from your administrator").fill("any-code");
    await page.getByRole("button", { name: "Continue" }).click();

    await expect
      .element(page.getByText("Too many attempts.", { exact: false }))
      .toBeInTheDocument();
  });
});
