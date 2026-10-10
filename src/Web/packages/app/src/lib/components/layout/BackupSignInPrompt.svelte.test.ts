import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import { flushIdle, resetIdle } from "$lib/test-stubs/when-idle";

let credentials: { hasSingleSignInMethod: boolean };
let marks: { markKey: string; status: string }[];
const updateStatus = vi.fn();
const listCredentials = vi.fn(() => remoteQuery(() => credentials));
const getAll = vi.fn(() => remoteQuery(() => marks));

vi.mock("$lib/utils/when-idle", () => import("$lib/test-stubs/when-idle"));

vi.mock("$lib/api/generated/passkeys.generated.remote", () => ({
  listCredentials: () => listCredentials(),
}));

vi.mock("$lib/api/generated/coachMarks.generated.remote", () => ({
  getAll: () => getAll(),
  updateStatus: (arg: unknown) => updateStatus(arg),
}));

import BackupSignInPrompt from "./BackupSignInPrompt.svelte";

function renderIdle() {
  render(BackupSignInPrompt);
  flushIdle();
}

const heading = () => page.getByText("Add a backup way to sign in");

describe("BackupSignInPrompt", () => {
  beforeEach(() => {
    credentials = { hasSingleSignInMethod: true };
    marks = [];
    updateStatus.mockReset();
    updateStatus.mockResolvedValue(undefined);
    listCredentials.mockClear();
    getAll.mockClear();
    resetIdle();
  });

  it("asks for nothing until the page is idle", async () => {
    render(BackupSignInPrompt);

    expect(listCredentials).not.toHaveBeenCalled();
    expect(getAll).not.toHaveBeenCalled();
    await expect.element(heading()).not.toBeInTheDocument();

    flushIdle();

    await expect.element(heading()).toBeVisible();
    expect(listCredentials).toHaveBeenCalled();
    expect(getAll).toHaveBeenCalled();
  });

  it("prompts when the account has one way in", async () => {
    renderIdle();

    await expect.element(heading()).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Account settings" }))
      .toBeVisible();
  });

  it("stays out of the way when the account has another way in", async () => {
    credentials = { hasSingleSignInMethod: false };

    renderIdle();

    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("stays out of the way once the prompt has been dismissed", async () => {
    marks = [{ markKey: "account.backup-sign-in", status: "dismissed" }];

    renderIdle();

    await expect.element(heading()).not.toBeInTheDocument();
  });

  it("ignores another mark's dismissal", async () => {
    marks = [{ markKey: "quick-tour.chart", status: "dismissed" }];

    renderIdle();

    await expect.element(heading()).toBeVisible();
  });

  it("persists the dismissal against the caller's subject", async () => {
    renderIdle();

    await page.getByRole("button", { name: "Dismiss" }).click();

    expect(updateStatus).toHaveBeenCalledWith({
      key: "account.backup-sign-in",
      request: { status: "dismissed" },
    });
  });
});
