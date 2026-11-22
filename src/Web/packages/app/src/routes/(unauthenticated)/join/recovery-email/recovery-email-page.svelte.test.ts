import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const setRecoveryEmail = vi.fn();
const updateStatus = vi.fn();
const goto = vi.fn();

vi.mock("#lib/api/generated/recoveryEmails.generated.remote.js", () => ({
  setRecoveryEmail: (arg: unknown) => setRecoveryEmail(arg),
}));
vi.mock("#lib/api/generated/coachMarks.generated.remote.js", () => ({
  updateStatus: (arg: unknown) => updateStatus(arg),
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: (...args: unknown[]) => goto(...args),
}));

import Page from "./+page.svelte";
import type { PageData } from "./$types";

const next = "/?welcome=1";

describe("join recovery email step", () => {
  beforeEach(() => {
    setRecoveryEmail.mockReset().mockResolvedValue({ email: "a@example.test", confirmed: false, sent: true });
    updateStatus.mockReset().mockResolvedValue({});
    goto.mockReset().mockResolvedValue(undefined);
  });

  it("explains what the address is for and offers save and skip alike", async () => {
    render(Page, { data: { next } as PageData });

    await expect.element(page.getByText(/If you lose this device/)).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save and send confirmation" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Skip for now" })).toBeVisible();
  });

  it("saves, sends and goes where joining went", async () => {
    render(Page, { data: { next } as PageData });

    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    expect(setRecoveryEmail).toHaveBeenCalledWith({ email: "a@example.test" });
    await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(next, { replace: true }));
    expect(updateStatus).not.toHaveBeenCalled();
  });

  it("skips to the same place and records the skip as a dismissal", async () => {
    render(Page, { data: { next } as PageData });

    await page.getByRole("button", { name: "Skip for now" }).click();

    await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(next, { replace: true }));
    expect(updateStatus).toHaveBeenCalledWith({
      key: "account.recovery-email",
      request: { status: "dismissed" },
    });
    expect(setRecoveryEmail).not.toHaveBeenCalled();
  });

  it("still skips when the dismissal cannot be recorded", async () => {
    updateStatus.mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    render(Page, { data: { next } as PageData });

    await page.getByRole("button", { name: "Skip for now" }).click();

    await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(next, { replace: true }));
  });

  it("shows the outcome and waits for Continue when the link was throttled", async () => {
    setRecoveryEmail.mockRejectedValue({ status: 429, body: { message: "x", code: "email_link_throttled" } });
    render(Page, { data: { next } as PageData });

    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    await expect.element(page.getByText(/send a link from account settings/)).toBeVisible();
    expect(page.getByText(/Resend/).elements()).toHaveLength(0);
    expect(goto).not.toHaveBeenCalled();
    await page.getByRole("button", { name: "Continue" }).click();
    expect(goto).toHaveBeenCalledWith(next, { replace: true });
  });

  it("shows the outcome and waits for Continue when the link could not be sent", async () => {
    setRecoveryEmail.mockRejectedValue({ status: 502, body: { message: "x", code: "email_delivery_failed" } });
    render(Page, { data: { next } as PageData });

    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    await expect.element(page.getByText(/we could not send the link/)).toBeVisible();
    expect(goto).not.toHaveBeenCalled();
  });

  it("stays on the step with the reason when the address is refused", async () => {
    setRecoveryEmail.mockRejectedValue({ status: 400, body: { message: "Enter a valid address." } });
    render(Page, { data: { next } as PageData });

    await page.getByLabelText("Recovery email").fill("a@example.test");
    await page.getByRole("button", { name: "Save and send confirmation" }).click();

    await expect.element(page.getByText("Enter a valid address.")).toBeVisible();
    expect(goto).not.toHaveBeenCalled();
  });
});
