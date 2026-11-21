import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach } from "vitest";
import { page as pageState } from "$app/state";
import { missingScopeError } from "#lib/forms/submit-messages.svelte.js";
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

  it("keeps the message of an error that carries no scope code", async () => {
    setError(404, { message: "Not Found" });

    render(ErrorPage, {});

    await expect.element(page.getByText("Not Found")).toBeVisible();
  });
});
