import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const updateStatus = vi.fn();

vi.mock("#lib/api/generated/coachMarks.generated.remote.js", () => ({
  updateStatus: (arg: unknown) => updateStatus(arg),
}));

import RecoveryEmailPrompt from "./RecoveryEmailPrompt.svelte";
import PromptWithMarker from "./RecoveryEmailPromptMarker.test.svelte";

describe("RecoveryEmailPrompt", () => {
  beforeEach(() => {
    updateStatus.mockReset();
    updateStatus.mockResolvedValue(undefined);
  });

  it("asks for an address and links to the settings section", async () => {
    render(RecoveryEmailPrompt, { show: true });

    await expect.element(page.getByText("Add a recovery email")).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Recovery email settings" }))
      .toHaveAttribute("href", "/settings/account#recovery-email");
  });

  it("asks to confirm when an address is waiting for its link", async () => {
    render(RecoveryEmailPrompt, { show: true, hasAddress: true });

    await expect.element(page.getByText("Confirm your recovery email")).toBeVisible();
    await expect
      .element(page.getByText("Your recovery email is not confirmed. Open recovery email settings to send a new link."))
      .toBeVisible();
    expect(page.getByText(/We sent a link/).elements()).toHaveLength(0);
  });

  it("renders nothing when it is not to be shown", async () => {
    render(PromptWithMarker, { show: false });
    await expect.element(page.getByText("marker")).toBeVisible();

    expect(page.getByText("Add a recovery email").elements()).toHaveLength(0);
  });

  it("stores the dismissal under the reminder's own key and leaves at once", async () => {
    render(RecoveryEmailPrompt, { show: true });

    await page.getByRole("button", { name: "Dismiss" }).click();

    expect(updateStatus).toHaveBeenCalledWith({
      key: "account.recovery-email",
      request: { status: "dismissed" },
    });
    await expect.element(page.getByText("Add a recovery email")).not.toBeInTheDocument();
  });

  it("comes back when the dismissal could not be saved", async () => {
    updateStatus.mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    render(RecoveryEmailPrompt, { show: true });

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.element(page.getByText("Add a recovery email")).toBeVisible();
  });
});
