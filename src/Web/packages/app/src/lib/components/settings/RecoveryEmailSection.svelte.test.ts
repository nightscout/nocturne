import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

let current: { available: boolean; email?: string | null; confirmed?: boolean };
const setRecoveryEmail = vi.fn();
const deleteRecoveryEmail = vi.fn();
const send = vi.fn();

vi.mock("#lib/api/generated/recoveryEmails.generated.remote.js", () => ({
  getRecoveryEmail: () => ({ current }),
  setRecoveryEmail: (arg: unknown) => setRecoveryEmail(arg),
  deleteRecoveryEmail: () => deleteRecoveryEmail(),
}));
vi.mock("#lib/api/generated/emailConfirmations.generated.remote.js", () => ({
  send: () => send(),
}));

import RecoveryEmailSection from "./RecoveryEmailSection.svelte";

describe("RecoveryEmailSection", () => {
  beforeEach(() => {
    setRecoveryEmail.mockReset().mockResolvedValue({ email: "a@example.test", confirmed: false, sent: true });
    deleteRecoveryEmail.mockReset().mockResolvedValue({ success: true });
    send.mockReset().mockResolvedValue({ success: true });
  });

  it("is hidden where the site cannot send the confirming email", async () => {
    current = { available: true, email: null };
    const shown = render(RecoveryEmailSection);
    await expect.element(page.getByText("Use an address that only you can read.", { exact: false })).toBeVisible();
    shown.unmount();

    current = { available: false };
    const { container } = render(RecoveryEmailSection);
    await Promise.resolve();

    expect(container.textContent?.trim()).toBe("");
  });

  it("asks for an address when there is none", async () => {
    current = { available: true, email: null, confirmed: false };
    render(RecoveryEmailSection);

    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    expect(setRecoveryEmail).toHaveBeenCalledWith({ email: "a@example.test" });
  });

  it("shows an unconfirmed address with resend, change and remove", async () => {
    current = { available: true, email: "a@example.test", confirmed: false };
    render(RecoveryEmailSection);

    await expect.element(page.getByText("a@example.test")).toBeVisible();
    await expect.element(page.getByText("Not confirmed yet")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Change" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Remove" })).toBeVisible();

    await page.getByRole("button", { name: "Resend" }).click();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("shows a confirmed address without a resend", async () => {
    current = { available: true, email: "a@example.test", confirmed: true };
    render(RecoveryEmailSection);

    await expect.element(page.getByText("Confirmed", { exact: true })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Change" })).toBeVisible();
    expect(page.getByRole("button", { name: "Resend" }).elements()).toHaveLength(0);
  });

  it("removes the address", async () => {
    current = { available: true, email: "a@example.test", confirmed: true };
    render(RecoveryEmailSection);

    await page.getByRole("button", { name: "Remove" }).click();

    expect(deleteRecoveryEmail).toHaveBeenCalledTimes(1);
  });

  it("changes the address through the same write", async () => {
    current = { available: true, email: "a@example.test", confirmed: true };
    render(RecoveryEmailSection);

    await page.getByRole("button", { name: "Change" }).click();
    await page.getByLabelText("Recovery email").fill("b@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    expect(setRecoveryEmail).toHaveBeenCalledWith({ email: "b@example.test" });
  });

  async function saveAddress() {
    render(RecoveryEmailSection);
    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();
  }

  it("says a link was sent only when the server sent one", async () => {
    current = { available: true, email: null, confirmed: false };
    await saveAddress();

    await expect.element(page.getByText("We sent a link to your address.")).toBeVisible();
  });

  it("says the address was saved and offers a later resend when the link was throttled", async () => {
    current = { available: true, email: null, confirmed: false };
    setRecoveryEmail.mockRejectedValue({ status: 429, body: { message: "x", code: "email_link_throttled" } });
    await saveAddress();

    await expect.element(page.getByText(/We saved your address. We sent a link a short time ago/)).toBeVisible();
    expect(page.getByText("We sent a link to your address.").elements()).toHaveLength(0);
  });

  it("says the address was saved but no link went out when delivery failed", async () => {
    current = { available: true, email: null, confirmed: false };
    setRecoveryEmail.mockRejectedValue({ status: 502, body: { message: "x", code: "email_delivery_failed" } });
    await saveAddress();

    await expect.element(page.getByText(/we could not send the link/)).toBeVisible();
    expect(page.getByText("We sent a link to your address.").elements()).toHaveLength(0);
  });

  it("says nothing about a link when the same confirmed address is saved again", async () => {
    current = { available: true, email: "a@example.test", confirmed: true };
    setRecoveryEmail.mockResolvedValue({ email: "a@example.test", confirmed: true, sent: false });
    render(RecoveryEmailSection);

    await page.getByRole("button", { name: "Change" }).click();
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    await expect.element(page.getByRole("button", { name: "Change" })).toBeVisible();
    expect(page.getByText(/link/).elements().filter((e) => e.closest("[role=status]"))).toHaveLength(0);
  });
});
