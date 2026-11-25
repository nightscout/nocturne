import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
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
    autofillAvailable: false,
    beforeAssertion: undefined as undefined | ((conditional: boolean) => Promise<void>),
  };
  return state;
});

vi.mock("./passkey-login", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./passkey-login")>()),
  runPasskeyAssertion: async (
    _options: unknown,
    submit: (assertion: { assertionResponseJson: string; challengeToken: string }) => Promise<unknown>,
    conditional = false
  ) => {
    await signIn.beforeAssertion?.(conditional);
    return submit({ assertionResponseJson: "{}", challengeToken: "login-token" });
  },
  offerPasskeyInAutofill: async (assertion: () => Promise<unknown>) =>
    signIn.autofillAvailable ? assertion() : null,
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

beforeEach(() => {
  signIn.autofillAvailable = false;
  signIn.beforeAssertion = undefined;
});

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

/**
 * The request a passkey sign-in makes finishes whether or not the form is still on screen, and
 * its first recovery codes are shown nowhere else. A dialog holding the form therefore cannot be
 * closed from the moment the sign-in starts until it ends without codes or the codes are saved.
 */
describe("LoginForm telling its dialog a passkey sign-in is in flight", () => {
  const RESULT_WITH_CODES = { success: true, accessToken: "t", recoveryCodes: ["AAAAA-BBBBB"] };

  function hold<T>() {
    let release!: (value: T) => void;
    let fail!: (reason: unknown) => void;
    const promise = new Promise<T>((resolve, reject) => {
      release = resolve;
      fail = reject;
    });
    return { promise, release, fail };
  }

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function lastBusy(onBusyChange: ReturnType<typeof vi.fn>) {
    return onBusyChange.mock.calls.at(-1)?.[0];
  }

  /** The dialog reports a close to its caller from a zero-delay timer, so one has to elapse. */
  async function nextMacrotask() {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  async function expectCannotClose(onCancel: ReturnType<typeof vi.fn>) {
    await expect.element(page.getByRole("button", { name: "Close" })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    // The press that follows a click inside the dialog is not read as outside it, so it takes two.
    for (let press = 0; press < 2; press++) {
      await userEvent.click(document.querySelector('[data-slot="dialog-overlay"]')!, {
        position: { x: 5, y: 5 },
      });
    }
    await nextMacrotask();
    await expect.element(page.getByText("Sign in required")).toBeVisible();
    expect(onCancel).not.toHaveBeenCalled();
  }

  beforeEach(() => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    providersLoading = false;
    providers = [];
    setLastSignIn(null);
    signIn.result = {};
    signIn.goToReturnUrl.mockClear();
    signIn.invalidateAll.mockClear();
    signIn.loginComplete.mockReset();
    signIn.loginComplete.mockImplementation(async () => signIn.result);
    signIn.setAuthCookies.mockReset();
    signIn.setAuthCookies.mockImplementation(async () => ({ success: true }));
  });

  it("cannot be closed while login/complete is in flight, and shows the codes it answers with", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });
    await expect.element(page.getByRole("button", { name: "Close" })).toBeVisible();

    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());

    await expectCannotClose(onCancel);

    request.release(RESULT_WITH_CODES);

    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
    await expectCannotClose(onCancel);
  });

  it("cannot be closed while the passkey ceremony is waiting on the person", async () => {
    const ceremony = hold<void>();
    signIn.beforeAssertion = () => ceremony.promise;
    signIn.result = RESULT_WITH_CODES;
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();

    await expectCannotClose(onCancel);
    expect(signIn.loginComplete).not.toHaveBeenCalled();

    ceremony.release();
    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
  });

  it("cannot be closed while the session cookies are being set", async () => {
    const cookies = hold<unknown>();
    signIn.setAuthCookies.mockImplementation(() => cookies.promise);
    signIn.result = RESULT_WITH_CODES;
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });

    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(signIn.setAuthCookies).toHaveBeenCalled());

    await expectCannotClose(onCancel);

    cookies.release({ success: true });
    await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
  });

  it("can be closed again when the sign-in is refused", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });
    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());
    await expect.element(page.getByRole("button", { name: "Close" })).not.toBeInTheDocument();

    request.release({ success: false, error: "That passkey is not recognised." });

    await expect.element(page.getByText("That passkey is not recognised.")).toBeVisible();
    await page.getByRole("button", { name: "Close" }).click();
    await vi.waitFor(() => expect(onCancel).toHaveBeenCalled());
  });

  it("can be closed again when the request fails", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });
    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());
    await expect.element(page.getByRole("button", { name: "Close" })).not.toBeInTheDocument();

    request.fail(new Error("network"));

    await expect.element(page.getByRole("button", { name: "Close" })).toBeVisible();
    await page.getByRole("button", { name: "Close" }).click();
    await vi.waitFor(() => expect(onCancel).toHaveBeenCalled());
  });

  it("reports not busy once a sign-in that issued no codes has handed back to the caller", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const onBusyChange = vi.fn();
    const onSuccess = vi.fn();
    const form = render(LoginForm, { props: { onBusyChange, onSuccess } });
    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(lastBusy(onBusyChange)).toBe(true));

    request.release({ success: true, accessToken: "t" });

    await vi.waitFor(() => expect(onSuccess).toHaveBeenCalled());
    await vi.waitFor(() => expect(lastBusy(onBusyChange)).toBe(false));
    form.unmount();
  });

  it("reports not busy when the form unmounts with a sign-in in flight", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const onBusyChange = vi.fn();
    const form = render(LoginForm, { props: { onBusyChange } });
    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(lastBusy(onBusyChange)).toBe(true));

    form.unmount();

    expect(lastBusy(onBusyChange)).toBe(false);
  });

  it("is not left locked when the dialog is closed from outside and opened again", async () => {
    const request = hold<Record<string, unknown>>();
    signIn.loginComplete.mockImplementation(() => request.promise);
    const dialog = render(LoginDialog, { props: { open: true } });
    await page.getByRole("button", { name: "Sign in with passkey" }).click();
    await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());

    await dialog.rerender({ open: false });
    await expect.element(page.getByText("Sign in required")).not.toBeInTheDocument();
    await dialog.rerender({ open: true });
    request.release(RESULT_WITH_CODES);
    await vi.waitFor(() => expect(signIn.setAuthCookies).toHaveBeenCalled());
    await nextMacrotask();

    await expect.element(page.getByRole("button", { name: "Close" })).toBeVisible();
    await userEvent.keyboard("{Escape}");
    await expect.element(page.getByText("Sign in required")).not.toBeInTheDocument();
  });

  it("stays locked through an explicit sign-in that aborted the pending autofill ceremony", async () => {
    signIn.autofillAvailable = true;
    const autofill = hold<void>();
    const explicit = hold<void>();
    signIn.beforeAssertion = (conditional) => {
      if (conditional) return autofill.promise;
      const abort = new Error("Ceremony was aborted");
      abort.name = "AbortError";
      autofill.fail(abort);
      return explicit.promise;
    };
    const onCancel = vi.fn();
    render(LoginDialog, { props: { open: true, onCancel } });
    await page.getByRole("button", { name: "Sign in with username" }).click();
    await expect.element(page.getByRole("button", { name: "Back" })).toBeEnabled();

    await page.getByPlaceholder("your-username").fill("example-user");
    await page.getByRole("button", { name: "Continue with passkey" }).click();
    await nextMacrotask();

    await expectCannotClose(onCancel);
    await expect.element(page.getByRole("button", { name: "Back" })).toBeDisabled();

    explicit.release();
    await expect.element(page.getByRole("button", { name: "Close" })).toBeVisible();
  });

  describe("through the browser's autofill dropdown", () => {
    async function openUsernameStep(props: Record<string, unknown>) {
      signIn.autofillAvailable = true;
      const form = render(LoginForm, { props });
      await page.getByRole("button", { name: "Sign in with username" }).click();
      return form;
    }

    it("is busy while it submits, keeps the form where it is, and shows the codes it receives", async () => {
      const request = hold<Record<string, unknown>>();
      signIn.loginComplete.mockImplementation(() => request.promise);
      const onBusyChange = vi.fn();
      await openUsernameStep({ onBusyChange });

      await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());

      await vi.waitFor(() => expect(lastBusy(onBusyChange)).toBe(true));
      await expect.element(page.getByRole("button", { name: "Back" })).toBeDisabled();

      request.release(RESULT_WITH_CODES);

      await expect.element(page.getByText("AAAAA-BBBBB")).toBeVisible();
      expect(lastBusy(onBusyChange)).toBe(true);
    });

    it("is not busy while the dropdown waits for the person to pick a passkey", async () => {
      const ceremony = hold<void>();
      signIn.beforeAssertion = () => ceremony.promise;
      const onBusyChange = vi.fn();

      await openUsernameStep({ onBusyChange });

      await expect.element(page.getByRole("button", { name: "Back" })).toBeEnabled();
      expect(lastBusy(onBusyChange)).toBe(false);
      expect(signIn.loginComplete).not.toHaveBeenCalled();
    });

    it("is not busy once a submit that failed has ended", async () => {
      signIn.loginComplete.mockRejectedValue(new Error("network"));
      const onBusyChange = vi.fn();

      await openUsernameStep({ onBusyChange });

      await vi.waitFor(() => expect(signIn.loginComplete).toHaveBeenCalled());
      await expect.element(page.getByRole("button", { name: "Back" })).toBeEnabled();
      expect(lastBusy(onBusyChange)).toBe(false);
    });
  });
});
