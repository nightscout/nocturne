import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
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

vi.mock("$routes/(unauthenticated)/auth/auth.remote", () => ({
  getOidcProviders: () => ({
    get loading() {
      return providersLoading;
    },
    get current() {
      return { enabled: providers.length > 0, providers };
    },
  }),
  setAuthCookies: vi.fn(),
  signInWithAuthenticator: {
    pending: 0,
    enhance: () => ({}),
    fields: { code: { issues: () => [] } },
  },
  signInWithRecoveryCode: {
    pending: 0,
    enhance: () => ({}),
    fields: {
      username: { issues: () => [] },
      code: { issues: () => [] },
    },
  },
}));

vi.mock("$lib/api/generated/passkeys.generated.remote", () => ({
  discoverableLoginOptions: vi.fn(),
  loginOptions: vi.fn(),
  loginComplete: vi.fn(),
}));

import LoginForm from "./LoginForm.svelte";

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
