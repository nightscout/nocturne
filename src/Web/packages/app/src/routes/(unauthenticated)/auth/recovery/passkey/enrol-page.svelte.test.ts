import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { remoteFormFields, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("https://acme.nocturne.example/auth/recovery/passkey?username=alex"),
  providers: [] as { id: string; name: string }[],
  emailRecoveryOffered: false,
  registerComplete: vi.fn(),
  loginOptions: vi.fn(),
  loginComplete: vi.fn(),
  startRecoveryLink: vi.fn(),
  startRegistration: vi.fn(),
  startAuthentication: vi.fn(),
  goToReturnUrl: vi.fn(async (_url: string, _options?: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    data: {},
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  invalidateAll: vi.fn(async () => {}),
}));
vi.mock("#lib/navigation/return-url.js", () => ({
  goToReturnUrl: (url: string, options?: unknown) => state.goToReturnUrl(url, options),
}));
vi.mock("@simplewebauthn/browser", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@simplewebauthn/browser")>()),
  startRegistration: (...args: unknown[]) => state.startRegistration(...args),
  startAuthentication: (...args: unknown[]) => state.startAuthentication(...args),
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  registerOptions: async () => ({ options: "{}", challengeToken: "reg-token" }),
  registerComplete: (request: unknown) => state.registerComplete(request),
  getAuthStatus: () => remoteQuery(() => ({ emailRecoveryOffered: state.emailRecoveryOffered })),
  loginOptions: (request: unknown) => state.loginOptions(request),
  discoverableLoginOptions: async () => ({ options: "{}", challengeToken: "login-token" }),
  loginComplete: (request: unknown) => state.loginComplete(request),
}));
vi.mock("#lib/api/generated/oidcs.generated.remote.js", () => ({
  startRecoveryLink: (request: unknown) => state.startRecoveryLink(request),
}));
vi.mock("#lib/api/generated/totps.generated.remote.js", () => ({
  getTrustedDevicePolicy: () => ({ current: { days: 0 } }),
}));
vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", async () => ({
  getOidcProviders: () => ({
    loading: false,
    get current() {
      return { enabled: state.providers.length > 0, providers: state.providers };
    },
  }),
  setAuthCookies: vi.fn(async () => ({ success: true })),
  signInWithAuthenticator: { pending: 0, enhance: () => ({}), fields: remoteFormFields() },
  signInWithRecoveryCode: { pending: 0, enhance: () => ({}), fields: remoteFormFields() },
}));
vi.mock("water-ui", async (importOriginal) => ({
  ...(await importOriginal<typeof import("water-ui")>()),
  Artwork: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import EnrolPage from "./+page.svelte";

const SETTINGS = "/settings/account?recovered=1";

describe("Recovery enrolment page", () => {
  beforeEach(() => {
    state.url = new URL("https://acme.nocturne.example/auth/recovery/passkey?username=alex");
    state.providers = [];
    state.emailRecoveryOffered = false;
    state.loginOptions.mockReset();
    state.loginOptions.mockResolvedValue({ options: "{}", challengeToken: "login-token" });
    state.registerComplete.mockReset();
    state.registerComplete.mockResolvedValue({ credentialId: "cred-1" });
    state.loginComplete.mockReset();
    state.loginComplete.mockImplementation(async () => ({ success: true, accessToken: "t" }));
    state.startRecoveryLink.mockReset();
    state.startRecoveryLink.mockResolvedValue({ authorizationUrl: "#provider-authorize" });
    state.startRegistration.mockReset();
    state.startRegistration.mockResolvedValue({ id: "attestation" });
    state.startAuthentication.mockReset();
    state.startAuthentication.mockResolvedValue({ id: "assertion" });
    state.goToReturnUrl.mockClear();
    window.location.hash = "";
  });

  it("states what the screen allows and for how long", async () => {
    render(EnrolPage);

    await expect
      .element(page.getByText("It lets you add one way to sign in for alex and nothing else.", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByText("This screen works for ten minutes.", { exact: false })).toBeInTheDocument();
  });

  it("reads correctly when the code named no username", async () => {
    state.url = new URL("https://acme.nocturne.example/auth/recovery/passkey?username=");
    render(EnrolPage);

    await expect
      .element(page.getByText("add one way to sign in for your account and nothing else", { exact: false }))
      .toBeInTheDocument();
  });

  it("offers no provider choice when none is configured", async () => {
    render(EnrolPage);

    await expect.element(page.getByRole("button", { name: "Create a passkey" })).toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: /Continue with/ })).not.toBeInTheDocument();
  });

  it("posts the chosen provider with a return URL and navigates to the address it returns", async () => {
    state.providers = [{ id: "7c1d2e3f-0000-4000-8000-000000000001", name: "Acme ID" }];
    render(EnrolPage);

    await page.getByRole("button", { name: "Continue with Acme ID" }).click();

    await vi.waitFor(() => expect(window.location.hash).toBe("#provider-authorize"));
    expect(state.startRecoveryLink).toHaveBeenCalledWith({
      providerId: "7c1d2e3f-0000-4000-8000-000000000001",
      returnUrl: SETTINGS,
    });
  });

  it("shows a refused provider link and leaves the passkey choice usable", async () => {
    state.providers = [{ id: "7c1d2e3f-0000-4000-8000-000000000001", name: "Acme ID" }];
    state.startRecoveryLink.mockImplementation(async () => error(400, "Bad Request"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Continue with Acme ID" }).click();

    await expect
      .element(page.getByText("We could not link that provider.", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create a passkey" })).toBeEnabled();
  });

  it("signs in with the new passkey and lands on settings naming it", async () => {
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await vi.waitFor(() => expect(state.goToReturnUrl).toHaveBeenCalled());
    expect(state.loginComplete).toHaveBeenCalledWith(
      expect.objectContaining({ challengeToken: "login-token" }),
    );
    expect(state.goToReturnUrl.mock.calls[0][0]).toBe(`${SETTINGS}&credentialId=cred-1`);
    expect(state.goToReturnUrl.mock.calls[0][1]).toMatchObject({ replace: true });
  });

  it("still reaches the assertion when the name it was given is a display name no account answers to", async () => {
    state.url = new URL("https://acme.nocturne.example/auth/recovery/passkey?username=Alex%20Display");
    state.loginOptions.mockRejectedValue(new Error("no account"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await vi.waitFor(() => expect(state.goToReturnUrl).toHaveBeenCalled());
    expect(state.startAuthentication).toHaveBeenCalled();
    expect(state.goToReturnUrl.mock.calls[0][0]).toBe(`${SETTINGS}&credentialId=cred-1`);
  });

  it("falls back to a button that signs in on this screen, without calling recovery a failure", async () => {
    state.startAuthentication.mockRejectedValueOnce(new DOMException("cancelled", "NotAllowedError"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await expect
      .element(page.getByText("Your passkey is ready. Sign in with it to finish.", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByText("could not register", { exact: false })).not.toBeInTheDocument();
    expect(state.goToReturnUrl).not.toHaveBeenCalled();
    const secondary = page.getByRole("link", { name: "Sign in on the sign-in page" });
    expect(decodeURIComponent(secondary.element().getAttribute("href") ?? "")).toContain(
      `returnUrl=${SETTINGS}&credentialId=cred-1`,
    );

    await page.getByRole("button", { name: "Sign in with your passkey" }).click();

    await vi.waitFor(() => expect(state.goToReturnUrl).toHaveBeenCalled());
    expect(state.goToReturnUrl.mock.calls[0][0]).toBe(`${SETTINGS}&credentialId=cred-1`);
  });

  it("shows the first recovery codes the sign-in issued and moves on only once they are saved", async () => {
    state.loginComplete.mockImplementation(async () => ({
      success: true,
      accessToken: "t",
      recoveryCodes: ["AAAAA-BBBBB", "CCCCC-DDDDD"],
    }));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await expect.element(page.getByText("AAAAA-BBBBB")).toBeInTheDocument();
    await expect.element(page.getByText("CCCCC-DDDDD")).toBeInTheDocument();
    expect(state.loginComplete).toHaveBeenCalledWith(expect.objectContaining({ showsRecoveryCodes: true }));
    await expect
      .element(page.getByText("You are signed in. Your account had no recovery codes, so we made them now."))
      .toBeVisible();
    await expect.element(page.getByText(/recovery code is now used up/)).not.toBeInTheDocument();
    const next = page.getByRole("button", { name: "Continue" });
    await expect.element(next).toBeDisabled();
    expect(state.goToReturnUrl).not.toHaveBeenCalled();

    await page.getByRole("checkbox").click();
    await next.click();

    await vi.waitFor(() => expect(state.goToReturnUrl).toHaveBeenCalled());
    expect(state.goToReturnUrl.mock.calls[0][0]).toBe(`${SETTINGS}&credentialId=cred-1`);
  });

  it("takes an account with an authenticator app to the second-factor step", async () => {
    state.loginComplete.mockImplementation(async () => ({
      success: true,
      totpRequired: true,
      stepUpToken: "step-up",
    }));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    const step = page.getByTestId("authenticator-step");
    await expect.element(step).toBeInTheDocument();
    const returnUrl = step.element().querySelector<HTMLInputElement>("input[type=hidden][name*=returnUrl]");
    expect(returnUrl?.value).toBe(`${SETTINGS}&credentialId=cred-1`);
    expect(state.goToReturnUrl).not.toHaveBeenCalled();
  });

  it("points to a new link, not the spent code, when the recovery session has ended", async () => {
    state.emailRecoveryOffered = true;
    state.registerComplete.mockImplementation(async () => error(401, "Unauthorized"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await expect.element(page.getByText("The recovery session has ended.")).toBeInTheDocument();
    await expect
      .element(page.getByRole("link", { name: "Request a new link" }))
      .toHaveAttribute("href", "/auth/recovery/email");
    await expect.element(page.getByText("recovery code or link again", { exact: false })).not.toBeInTheDocument();
  });

  it("points to an administrator when the session has ended and email recovery is not offered", async () => {
    state.registerComplete.mockImplementation(async () => error(401, "Unauthorized"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await expect
      .element(page.getByTestId("recovery-code-next-step"))
      .toHaveTextContent("Ask an administrator for a new code.");
  });

  it("keeps the choices usable when enrolment is refused", async () => {
    state.registerComplete.mockImplementation(async () => error(400, "Passkey registration failed"));
    render(EnrolPage);

    await page.getByRole("button", { name: "Create a passkey" }).click();

    await expect
      .element(page.getByText("Passkey registration failed", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create a passkey" })).toBeEnabled();
    expect(state.loginComplete).not.toHaveBeenCalled();
  });
});
