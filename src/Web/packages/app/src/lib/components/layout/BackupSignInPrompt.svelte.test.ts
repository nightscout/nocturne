import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const updateStatus = vi.fn();

vi.mock("#lib/api/generated/coachMarks.generated.remote.js", () => ({
  updateStatus: (arg: unknown) => updateStatus(arg),
}));

import BackupSignInPrompt from "./BackupSignInPrompt.svelte";

const heading = () => page.getByText("Add a backup way to sign in");

describe("BackupSignInPrompt", () => {
  beforeEach(() => {
    updateStatus.mockReset();
    updateStatus.mockResolvedValue(undefined);
  });

  it("prompts from its first render when the account has one way in", async () => {
    render(BackupSignInPrompt, { show: true });

    await expect.element(heading()).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Account settings" }))
      .toBeVisible();
  });

  it("stays out of the way when it is not to be shown", async () => {
    render(BackupSignInPrompt, { show: false });

    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("persists the dismissal and leaves at once", async () => {
    render(BackupSignInPrompt, { show: true });

    await page.getByRole("button", { name: "Dismiss" }).click();

    expect(updateStatus).toHaveBeenCalledWith({
      key: "account.backup-sign-in",
      request: { status: "dismissed" },
    });
    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("comes back when the dismissal could not be saved", async () => {
    updateStatus.mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    render(BackupSignInPrompt, { show: true });

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.element(heading()).toBeVisible();
  });
});
