import { afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";

vi.mock("./LoginForm.svelte", async () => ({
  default: (await import("#lib/test-stubs/LoginForm.test-stub.svelte")).default,
}));

import ConfirmIdentityDialog from "./ConfirmIdentityDialog.svelte";
import SignInGate from "./SignInGate.svelte";
import { confirmIdentity } from "./confirm-identity.svelte";

/**
 * A session opened from a sign-in link someone else issued must be confirmed by the person's own
 * sign-in before it may change sign-in methods, credentials or sharing. The gate offers that
 * sign-in up front, the dialog runs it in place, and the caller learns whether it happened so it
 * can do the action again.
 */
describe("the confirm-it-is-you prompt", () => {
  afterEach(() => {
    confirmIdentity.settle(false);
  });

  it("stays closed until something asks for it", async () => {
    render(ConfirmIdentityDialog);

    await expect.element(browserPage.getByText("Confirm it is you")).not.toBeInTheDocument();
  });

  it("opens from the gate, with the reason on the gate and the sign-in returning to this page", async () => {
    page.url = new URL("http://localhost/settings/connectors?tab=apps") as typeof page.url;
    render(ConfirmIdentityDialog);
    render(SignInGate, { props: { reason: "Sign in again to see your apps." } });

    await expect.element(browserPage.getByText("Sign in again to see your apps.")).toBeVisible();
    await browserPage.getByRole("button", { name: "Sign in" }).click();

    await expect.element(browserPage.getByText("Confirm it is you")).toBeVisible();
    await expect
      .element(browserPage.getByTestId("login-form-return-url"))
      .toHaveTextContent("/settings/connectors?tab=apps");
  });

  it("closes and tells the caller the person signed in, so it can do the action again", async () => {
    render(ConfirmIdentityDialog);
    const asked = confirmIdentity.request();

    await browserPage.getByRole("button", { name: "Finish sign-in" }).click();

    await expect(asked).resolves.toBe(true);
    await expect.element(browserPage.getByText("Confirm it is you")).not.toBeInTheDocument();
  });

  it("tells the caller nothing changed when the person closes it", async () => {
    render(ConfirmIdentityDialog);
    const asked = confirmIdentity.request();
    await expect.element(browserPage.getByText("Confirm it is you")).toBeVisible();

    await browserPage.getByRole("button", { name: "Close" }).click();

    await expect(asked).resolves.toBe(false);
  });
});
