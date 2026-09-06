import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let credentials: { hasSingleSignInMethod: boolean };
let marks: { markKey: string; status: string }[];
const updateStatus = vi.fn();

vi.mock("$lib/api/generated/passkeys.generated.remote", () => ({
  listCredentials: () => remoteQuery(() => credentials),
}));

vi.mock("$lib/api/generated/coachMarks.generated.remote", () => ({
  getAll: () => remoteQuery(() => marks),
  updateStatus: (arg: unknown) => updateStatus(arg),
}));

import BackupSignInPrompt from "./BackupSignInPrompt.svelte";

const heading = () => page.getByText("Add a backup way to sign in");

describe("BackupSignInPrompt", () => {
  beforeEach(() => {
    credentials = { hasSingleSignInMethod: true };
    marks = [];
    updateStatus.mockReset();
    updateStatus.mockResolvedValue(undefined);
  });

  it("prompts when the account has one way in", async () => {
    render(BackupSignInPrompt);

    await expect.element(heading()).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Account settings" }))
      .toBeVisible();
  });

  it("stays out of the way when the account has another way in", async () => {
    credentials = { hasSingleSignInMethod: false };

    render(BackupSignInPrompt);

    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("stays out of the way once the prompt has been dismissed", async () => {
    marks = [{ markKey: "account.backup-sign-in", status: "dismissed" }];

    render(BackupSignInPrompt);

    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("ignores another mark's dismissal", async () => {
    marks = [{ markKey: "quick-tour.chart", status: "dismissed" }];

    render(BackupSignInPrompt);

    await expect.element(heading()).toBeVisible();
  });

  it("persists the dismissal against the caller's subject", async () => {
    render(BackupSignInPrompt);

    await page.getByRole("button", { name: "Dismiss" }).click();

    expect(updateStatus).toHaveBeenCalledWith({
      key: "account.backup-sign-in",
      request: { status: "dismissed" },
    });
  });
});
