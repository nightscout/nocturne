import { afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage, userEvent } from "vitest/browser";
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

  it("cannot be closed while a sign-in is in flight, since its result has nowhere else to go", async () => {
    render(ConfirmIdentityDialog);
    const asked = confirmIdentity.request();
    const settled = vi.fn();
    void asked.then(settled);
    await expect.element(browserPage.getByRole("button", { name: "Close" })).toBeVisible();

    await browserPage.getByRole("button", { name: "Start sign-in" }).click();

    await expect.element(browserPage.getByRole("button", { name: "Close" })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await userEvent.click(document.querySelector('[data-slot="dialog-overlay"]')!, {
      position: { x: 5, y: 5 },
    });
    await expect.element(browserPage.getByText("Confirm it is you")).toBeVisible();
    expect(settled).not.toHaveBeenCalled();

    await browserPage.getByRole("button", { name: "Finish sign-in" }).click();

    await expect(asked).resolves.toBe(true);
  });

  it("can be closed again once a sign-in that did not finish has ended", async () => {
    render(ConfirmIdentityDialog);
    const asked = confirmIdentity.request();
    await browserPage.getByRole("button", { name: "Start sign-in" }).click();
    await expect.element(browserPage.getByRole("button", { name: "Close" })).not.toBeInTheDocument();

    await browserPage.getByRole("button", { name: "Fail sign-in" }).click();

    await browserPage.getByRole("button", { name: "Close" }).click();
    await expect(asked).resolves.toBe(false);
  });

  it("is not left locked when it opens again after it was closed with a sign-in in flight", async () => {
    render(ConfirmIdentityDialog);
    const first = confirmIdentity.request();
    await browserPage.getByRole("button", { name: "Start sign-in" }).click();
    confirmIdentity.settle(false);
    await expect(first).resolves.toBe(false);
    await expect.element(browserPage.getByText("Confirm it is you")).not.toBeInTheDocument();

    const second = confirmIdentity.request();

    await expect.element(browserPage.getByRole("button", { name: "Close" })).toBeVisible();
    await userEvent.keyboard("{Escape}");
    await expect(second).resolves.toBe(false);
  });

  it("tells the caller nothing changed when the person closes it", async () => {
    render(ConfirmIdentityDialog);
    const asked = confirmIdentity.request();
    await expect.element(browserPage.getByText("Confirm it is you")).toBeVisible();

    await browserPage.getByRole("button", { name: "Close" }).click();

    await expect(asked).resolves.toBe(false);
  });
});
