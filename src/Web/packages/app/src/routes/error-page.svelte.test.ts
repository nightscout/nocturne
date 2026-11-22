import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach } from "vitest";
import { page as pageState } from "$app/state";
import { missingScopeError, signInRequiredError } from "#lib/forms/submit-messages.svelte.js";
import { confirmIdentity } from "#lib/components/auth/confirm-identity.svelte.js";
import ErrorPage from "./+error.svelte";

const scopeSentence = "This operation requires the 'tenant.settings' scope.";

function setError(status: number, error: Record<string, unknown> | null) {
  Object.assign(pageState, { status, error });
}

describe("the error page", () => {
  beforeEach(() => setError(200, null));

  it("shows the app's sentence for a missing_scope 403, never the scope", async () => {
    setError(403, { message: scopeSentence, code: "missing_scope" });

    const { container } = render(ErrorPage, {});

    await expect.element(page.getByText(missingScopeError())).toBeVisible();
    expect(container.textContent).not.toContain("tenant.settings");
  });

  // The page formats the error inside `$derived`, where a state write throws, so a load refused
  // with sign_in_required renders the sentence and the sign-in button and opens nothing by itself.
  it("offers the sign-in for a sign_in_required 403 without opening the prompt", async () => {
    setError(403, {
      message: "This session was opened from a sign-in link.",
      code: "sign_in_required",
    });

    render(ErrorPage, {});

    await expect.element(page.getByText(signInRequiredError())).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Sign in" })).toBeVisible();
    expect(confirmIdentity.open).toBe(false);
  });

  it("keeps the message of an error that carries no scope code", async () => {
    setError(404, { message: "Not Found" });

    render(ErrorPage, {});

    await expect.element(page.getByText("Not Found")).toBeVisible();
  });
});
