import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

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

describe("LoginForm on a tenantless host", () => {
  beforeEach(() => {
    providers = [];
    providersLoading = false;
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
