import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("https://acme.nocturne.example/auth/error"),
  emailRecoveryOffered: false,
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
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  getAuthStatus: () => remoteQuery(() => ({ emailRecoveryOffered: state.emailRecoveryOffered })),
}));

import ErrorPage from "./+page.svelte";

function visit(code: string, flow?: "recovery") {
  state.url = new URL(`https://acme.nocturne.example/auth/error?${new URLSearchParams({ error: code, ...(flow ? { flow } : {}) })}`);
  return render(ErrorPage);
}

describe("Authentication error page", () => {
  beforeEach(() => {
    state.emailRecoveryOffered = false;
  });

  it.each([
    "identity_already_linked",
    "access_denied",
    "invalid_state",
    "expired_state",
    "a_code_only_the_provider_knows",
    "recovery_session_required",
  ])("offers the way back to the enrol screen for %s refused in a recovery", async (code) => {
    visit(code, "recovery");

    await expect
      .element(page.getByRole("link", { name: "Back to add a way to sign in" }))
      .toHaveAttribute("href", "/auth/recovery/passkey");
  });

  it.each([
    "invalid_state",
    "expired_state",
    "access_denied",
  ])("does not offer it for %s from an ordinary sign-in", async (code) => {
    visit(code);

    await expect.element(page.getByRole("link", { name: "Try again" })).toBeInTheDocument();
    await expect.element(page.getByRole("link", { name: "Back to add a way to sign in" })).not.toBeInTheDocument();
  });

  it("explains an ended recovery session and points to an administrator when email recovery is not offered", async () => {
    visit("recovery_session_required");

    await expect.element(page.getByText("Recovery ended")).toBeInTheDocument();
    await expect
      .element(page.getByTestId("recovery-code-next-step"))
      .toHaveTextContent("Ask an administrator for a new code.");
  });

  it("points an ended recovery session to a new link when email recovery is offered", async () => {
    state.emailRecoveryOffered = true;
    visit("recovery_session_required");

    await expect
      .element(page.getByRole("link", { name: "Request a new link" }))
      .toHaveAttribute("href", "/auth/recovery/email");
  });
});
