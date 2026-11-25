import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({
  setupRequired: true,
  ownerOptions: vi.fn(),
  ownerComplete: vi.fn(),
  setAuthCookies: vi.fn(),
  startRegistration: vi.fn(),
}));

vi.mock("@simplewebauthn/browser", () => ({ startRegistration: mocks.startRegistration }));

vi.mock("../setup.remote", () => ({
  setupOwnerOptions: (arg: unknown) => remoteCommand(mocks.ownerOptions)(arg),
  setupOwnerComplete: (arg: unknown) => remoteCommand(mocks.ownerComplete)(arg),
  setupOwnerOidc: vi.fn(),
  validateSetupUsername: () => remoteQuery(() => ({ isValid: true })),
}));

vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  getAuthStatus: () => remoteQuery(() => ({ setupRequired: mocks.setupRequired })),
  loginComplete: vi.fn(),
}));

vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
  getAuthState: () => remoteQuery(() => ({ isAuthenticated: false })),
  getOidcProviders: () => remoteQuery(() => ({ enabled: false, providers: [] })),
  setAuthCookies: (arg: unknown) => remoteCommand(mocks.setAuthCookies)(arg),
}));

import AccountCreation from "./AccountCreation.svelte";

function refusal(status: number, message: string): unknown {
  try {
    error(status, message);
  } catch (thrown) {
    return thrown;
  }
}

async function createAccount() {
  await page.getByLabelText("Display name").fill("Alex");
  await page.getByLabelText("Username").fill("alex");
  await expect.element(page.getByText("Available")).toBeVisible();
  await page.getByRole("button", { name: "Create account with passkey" }).click();
}

const SIGN_IN_INSTEAD =
  "Your passkey is saved, but setup did not finish. Sign in with the passkey you made to continue.";

/**
 * Setup closes when the owner's passkey is stored. When a later step fails, another attempt here is
 * refused, so the step sends the owner to sign in with the passkey, which also gives them their
 * recovery codes.
 */
describe("setup account step after a failure", () => {
  beforeEach(() => {
    mocks.setupRequired = true;
    for (const fn of [mocks.ownerOptions, mocks.ownerComplete, mocks.setAuthCookies, mocks.startRegistration]) {
      fn.mockReset();
    }
    mocks.ownerOptions.mockResolvedValue({ options: "{}", challengeToken: "setup-token" });
    mocks.startRegistration.mockResolvedValue({ id: "cred-1" });
  });

  it("sends the owner to sign in when completion failed after the passkey was stored", async () => {
    mocks.ownerComplete.mockRejectedValue(refusal(500, "Failed to owner complete"));
    mocks.setupRequired = false;
    render(AccountCreation, { props: { onComplete: vi.fn() } });

    await createAccount();

    await expect.element(page.getByText(SIGN_IN_INSTEAD)).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Go to sign in" }))
      .toHaveAttribute("href", expect.stringContaining("auth/login?returnUrl=%2Fsetup"));
    await expect.element(page.getByText(/Try again/)).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create account with passkey" })).not.toBeInTheDocument();
  });

  it("sends the owner to sign in when a retry is refused because the owner exists", async () => {
    mocks.setupRequired = false;
    mocks.ownerOptions.mockRejectedValue(refusal(409, "Request rejected"));
    render(AccountCreation, { props: { onComplete: vi.fn() } });

    await createAccount();

    await expect.element(page.getByText(SIGN_IN_INSTEAD)).toBeVisible();
    expect(mocks.startRegistration).not.toHaveBeenCalled();
  });

  it("does not say a passkey was saved when a conflict left setup open", async () => {
    mocks.ownerOptions.mockRejectedValue(refusal(409, "Request rejected"));
    render(AccountCreation, { props: { onComplete: vi.fn() } });

    await createAccount();

    await expect.element(page.getByText("Request rejected")).toBeVisible();
    await expect.element(page.getByText(SIGN_IN_INSTEAD)).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create account with passkey" })).toBeVisible();
  });

  it("still shows the recovery codes when the session cookies could not be set, since they are shown nowhere else", async () => {
    mocks.ownerComplete.mockResolvedValue({
      success: true,
      accessToken: "t",
      recoveryCodes: ["AAAAA-BBBBB", "CCCCC-DDDDD"],
    });
    mocks.setAuthCookies.mockRejectedValue(new Error("network"));
    const onComplete = vi.fn();
    render(AccountCreation, { props: { onComplete } });

    await createAccount();

    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    await expect.element(page.getByText("CCCCC-DDDDD")).toBeVisible();
    expect(mocks.setAuthCookies).toHaveBeenCalled();
    await expect.element(page.getByText(/Try again/)).not.toBeInTheDocument();

    await page.getByRole("checkbox").click();
    await page.getByRole("button", { name: "Continue Setup" }).click();
    expect(onComplete).toHaveBeenCalled();
  });

  it("asks for another attempt when the failure left setup open", async () => {
    mocks.ownerComplete.mockRejectedValue(refusal(500, "Failed to owner complete"));
    render(AccountCreation, { props: { onComplete: vi.fn() } });

    await createAccount();

    await expect.element(page.getByText(/Try again/)).toBeVisible();
    await expect.element(page.getByText(SIGN_IN_INSTEAD)).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create account with passkey" })).toBeVisible();
  });
});
