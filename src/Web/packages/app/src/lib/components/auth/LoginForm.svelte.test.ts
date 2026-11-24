import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { page as appState } from "$app/state";
import type { LastSignIn } from "./last-sign-in";

/**
 * On a tenantless host every passkey, authenticator and recovery-code control is hidden,
 * because each is checked against a resolved tenant's members. That leaves the identity
 * providers as the only thing that can sign anyone in, so these pin that the provider
 * buttons actually render there — and that the page says what to do when there are none.
 */

interface StubProvider {
  id: string;
  name: string;
  icon?: string;
  buttonColor?: string;
}

let providers: StubProvider[] = [];
let providersLoading = false;

vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", async () => ({
  getOidcProviders: () => ({
    get loading() {
      return providersLoading;
    },
    get current() {
      return { enabled: providers.length > 0, providers };
    },
  }),
  setAuthCookies: (request: unknown) => signIn.setAuthCookies(request),
  signInWithAuthenticator: {
    pending: 0,
    enhance: () => ({}),
    fields: (await import("#lib/test-stubs/remote-resource.js")).remoteFormFields(),
  },
  signInWithRecoveryCode: {
    pending: 0,
    enhance: () => ({}),
    fields: (await import("#lib/test-stubs/remote-resource.js")).remoteFormFields(),
  },
}));

let trustedDeviceDays = 30;

vi.mock("#lib/api/generated/totps.generated.remote.js", () => ({
  getTrustedDevicePolicy: () => ({
    get current() {
      return { days: trustedDeviceDays };
    },
  }),
}));

vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  discoverableLoginOptions: vi.fn(),
  loginOptions: vi.fn(),
  loginComplete: (request: unknown) => signIn.loginComplete(request),
}));

const signIn = vi.hoisted(() => {
  const state = {
    result: {} as Record<string, unknown>,
    loginComplete: vi.fn(async (_request: unknown) => state.result),
    setAuthCookies: vi.fn(async (_request: unknown): Promise<unknown> => ({ success: true })),
    goToReturnUrl: vi.fn(async (_url: string, _options?: unknown) => {}),
    invalidateAll: vi.fn(async () => {}),
  };
  return state;
});

vi.mock("./passkey-login", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./passkey-login")>()),
  runPasskeyAssertion: async (
    _options: unknown,
    submit: (assertion: { assertionResponseJson: string; challengeToken: string }) => Promise<unknown>
  ) => submit({ assertionResponseJson: "{}", challengeToken: "login-token" }),
}));
vi.mock("#lib/navigation/return-url.js", () => ({
  goToReturnUrl: (url: string, options?: unknown) => signIn.goToReturnUrl(url, options),
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  invalidateAll: () => signIn.invalidateAll(),
}));

import LoginForm from "./LoginForm.svelte";
import LoginDialog from "./login-dialog.svelte";

/** The layout server load puts the parsed hint cookie here; the form only reads it. */
function setLastSignIn(hint: LastSignIn | null) {
  appState.data = { lastSignIn: hint };
}

/** The visible sign-in buttons, in the order they are rendered. */
async function buttonOrder(): Promise<string[]> {
  const buttons = await page.getByRole("button").elements();
  return buttons.map((b) => b.textContent?.replace(/\s+/g, " ").trim() ?? "");
}

describe("LoginForm on a tenantless host", () => {
  beforeEach(() => {
    providers = [];
    providersLoading = false;
    setLastSignIn(null);
  });

  it("renders a provider sign-in button when a provider is available", async () => {
    providers = [{ id: "provider-1", name: "Example SSO" }];

    render(LoginForm, { props: { tenantless: true } });

    // The only sign-in affordance the tenantless page can offer.
    await expect
      .element(page.getByRole("button", { name: "Sign in with Example SSO" }))
      .toBeVisible();
  });

  it("tells the visitor where to sign in when no provider is available", async () => {
    providers = [];

    render(LoginForm, { props: { tenantless: true } });

    await expect
      .element(page.getByText("Open your tenant's address to sign in."))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: /^Sign in with/ }))
      .not.toBeInTheDocument();
  });
});

/**
 * The form leads with whichever method last worked, so somebody who signs in with an identity
 * provider is not shown the passkey button first on every visit. The hint is a cookie the API
 * writes next to the session cookies; the layout load parses it into page data.
 */
describe("LoginForm ordering by last-used method", () => {
  const GOOGLE = "2f6a4c9e-1d3b-4a58-9f27-8c0b5e6d1a44";
  const GITHUB = "9b1c33d0-77aa-4e12-b0d5-0c4f2a91e775";

  beforeEach(() => {
    providersLoading = false;
    providers = [
      { id: GOOGLE, name: "Google" },
      { id: GITHUB, name: "GitHub" },
    ];
    setLastSignIn(null);
  });

  it("leads with the passkey button when nothing has been used yet", async () => {
    render(LoginForm, { props: {} });

    const order = await buttonOrder();
    expect(order[0]).toBe("Sign in with passkey");
    expect(order.indexOf("Sign in with Google")).toBeGreaterThan(
      order.indexOf("Sign in with username")
    );
    await expect
      .element(page.getByText("Last used"))
      .not.toBeInTheDocument();
  });

  it("leads with the provider that last worked, and says so", async () => {
    setLastSignIn({ method: "oidc", providerId: GITHUB });

    render(LoginForm, { props: {} });

    const order = await buttonOrder();
    expect(order[0]).toBe("Sign in with GitHub Last used");
    // Ahead of the other provider and ahead of both passkey controls.
    expect(order.indexOf("Sign in with Google")).toBeGreaterThan(0);
    expect(order.indexOf("Sign in with passkey")).toBeGreaterThan(0);

    // One badge, on one button.
    await expect.element(page.getByText("Last used")).toBeVisible();
    expect(order.filter((label) => label.includes("Last used"))).toHaveLength(1);
  });

  it("badges the passkey button when that is what last worked", async () => {
    setLastSignIn({ method: "passkey", providerId: null });

    render(LoginForm, { props: {} });

    const order = await buttonOrder();
    expect(order[0]).toBe("Sign in with passkey Last used");
    expect(order.filter((label) => label.includes("Last used"))).toHaveLength(1);
  });

  it("keeps the operator's provider order when the hint names none of them", async () => {
    setLastSignIn({ method: "oidc", providerId: "00000000-0000-0000-0000-000000000000" });

    render(LoginForm, { props: {} });

    const order = await buttonOrder();
    expect(order[0]).toBe("Sign in with passkey");
    expect(order.indexOf("Sign in with Google")).toBeLessThan(
      order.indexOf("Sign in with GitHub")
    );
  });

  it("badges the provider on a tenantless host too", async () => {
    setLastSignIn({ method: "oidc", providerId: GITHUB });

    render(LoginForm, { props: { tenantless: true } });

    const order = await buttonOrder();
    expect(order[0]).toBe("Sign in with GitHub Last used");
    // Nothing tenant-scoped is offered here, so the badge has to survive on its own.
    expect(order).not.toContain("Sign in with passkey");
  });
});

/**
 * An identity-provider sign-in for an account with an authenticator lands back on the login page
 * with a step-up token, and the form must open on the code entry rather than the sign-in buttons.
 */
describe("LoginForm after an identity provider asks for the authenticator code", () => {
  beforeEach(() => {
    providersLoading = false;
    providers = [{ id: "provider-1", name: "Example SSO" }];
    trustedDeviceDays = 30;
    setLastSignIn(null);
  });

  it("offers to trust the device, off until the visitor selects it", async () => {
    render(LoginForm, { props: { pendingStepUpToken: "step-up-token" } });

    const trust = page.getByRole("checkbox", { name: "Trust this device for 30 days" });
    await expect.element(trust).toBeVisible();
    await expect.element(trust).not.toBeChecked();
  });

  it("does not offer to trust the device when the instance turned that off", async () => {
    trustedDeviceDays = 0;

    render(LoginForm, { props: { pendingStepUpToken: "step-up-token" } });

    await expect.element(page.getByText("Authenticator code")).toBeVisible();
    await expect.element(page.getByRole("checkbox")).not.toBeInTheDocument();
  });

  it.each([false, true])("opens on the code entry (tenantless: %s)", async (tenantless) => {
    render(LoginForm, { props: { tenantless, pendingStepUpToken: "step-up-token" } });

    await expect.element(page.getByText("Authenticator code")).toBeVisible();
    await expect
      .element(page.getByRole("button", { name: /^Sign in with/ }))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByText(/checked against one\s+tenant/))
      .not.toBeInTheDocument();
    expect(
      document.querySelector<HTMLInputElement>('input[name="stepUpToken"]')?.value
    ).toBe("step-up-token");
  });

  it("opens on the sign-in buttons without a token", async () => {
    render(LoginForm, { props: {} });

    await expect
      .element(page.getByRole("button", { name: "Sign in with Example SSO" }))
      .toBeVisible();
    await expect.element(page.getByText("Authenticator code")).not.toBeInTheDocument();
  });
});

/**
 * A passkey sign-in for a member who has never held recovery codes answers with their first set,
 * which the API shows nowhere else, so the form keeps them on screen until they are saved.
 */
describe("LoginForm after a passkey sign-in that issued the first recovery codes", () => {
  beforeEach(() => {
    providersLoading = false;
    providers = [];
    setLastSignIn(null);
    signIn.goToReturnUrl.mockClear();
    signIn.invalidateAll.mockClear();
    signIn.loginComplete.mockClear();
    signIn.setAuthCookies.mockReset();
    signIn.setAuthCookies.mockImplementation(async () => ({ success: true }));
  });

  it("asks for the first codes, since it shows them", async () => {
    signIn.result = { success: true, accessToken: "t" };
    render(LoginForm, { props: { returnUrl: "/reports" } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();

    await vi.waitFor(() =>
      expect(signIn.loginComplete).toHaveBeenCalledWith(expect.objectContaining({ showsRecoveryCodes: true }))
    );
  });

  it("shows the codes and moves on only once they are saved", async () => {
    signIn.result = { success: true, accessToken: "t", recoveryCodes: ["AAAAA-BBBBB", "CCCCC-DDDDD"] };
    render(LoginForm, { props: { returnUrl: "/reports" } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();

    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    await expect.element(page.getByText("CCCCC-DDDDD")).toBeVisible();
    const next = page.getByRole("button", { name: "Continue" });
    await expect.element(next).toBeDisabled();
    expect(signIn.goToReturnUrl).not.toHaveBeenCalled();
    expect(signIn.invalidateAll).not.toHaveBeenCalled();

    await page.getByRole("checkbox").click();
    await next.click();

    await vi.waitFor(() => expect(signIn.goToReturnUrl).toHaveBeenCalledWith("/reports", { refreshAll: true }));
  });

  it("refreshes the page data before handing a dialog's sign-in back, once the codes are saved", async () => {
    signIn.result = { success: true, accessToken: "t", recoveryCodes: ["AAAAA-BBBBB"] };
    const onSuccess = vi.fn();
    render(LoginForm, { props: { onSuccess } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    expect(onSuccess).not.toHaveBeenCalled();

    await page.getByRole("checkbox").click();
    await page.getByRole("button", { name: "Continue" }).click();

    await vi.waitFor(() => expect(onSuccess).toHaveBeenCalled());
    expect(signIn.invalidateAll).toHaveBeenCalled();
  });

  it("still shows the codes when the session cookies could not be set, since they are stored and shown nowhere else", async () => {
    signIn.setAuthCookies.mockRejectedValue(new Error("network"));
    signIn.result = { success: true, accessToken: "t", recoveryCodes: ["AAAAA-BBBBB"] };
    render(LoginForm, { props: { returnUrl: "/reports" } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();

    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    expect(signIn.setAuthCookies).toHaveBeenCalled();
    await expect.element(page.getByText(/sign-in failed/i)).not.toBeInTheDocument();
  });

  it("keeps the sign-in dialog open while the codes are on screen", async () => {
    signIn.result = { success: true, accessToken: "t", recoveryCodes: ["AAAAA-BBBBB"] };
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });
    await expect.element(page.getByRole("button", { name: "Close" })).toBeVisible();

    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();

    await expect.element(page.getByRole("button", { name: "Close" })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await userEvent.click(document.querySelector('[data-slot="dialog-overlay"]')!, {
      position: { x: 5, y: 5 },
    });
    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    expect(onCancel).not.toHaveBeenCalled();
  });

  it("moves straight on when the sign-in issued no codes", async () => {
    signIn.result = { success: true, accessToken: "t" };
    render(LoginForm, { props: { returnUrl: "/reports" } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();

    await vi.waitFor(() => expect(signIn.goToReturnUrl).toHaveBeenCalledWith("/reports", { refreshAll: true }));
    await expect.element(page.getByText("Recovery codes")).not.toBeInTheDocument();
  });
});
