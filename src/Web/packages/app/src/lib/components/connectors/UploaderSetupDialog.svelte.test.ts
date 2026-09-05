import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach } from "vitest";
import { vi } from "vitest";
import { UploaderPlatform, type UploaderApp } from "$api-clients";

let createImpl: () => Promise<{ token?: string }>;

vi.mock("$lib/api/generated/directGrants.generated.remote", () => ({
  list: () => Promise.resolve([]),
  create: () => createImpl(),
  revoke: () => Promise.resolve(),
}));

import Wrapper from "./uploader-setup-dialog-test-wrapper.svelte";

const JUGGLUCO: UploaderApp = {
  id: "juggluco",
  platform: UploaderPlatform.Android,
};

const instructions = () => page.getByText("Set up Juggluco");
const tokenDialog = () => page.getByRole("dialog");

describe("uploader setup to API token hand-off", () => {
  beforeEach(() => {
    createImpl = () => Promise.resolve({ token: "noc_created" });
  });

  it("opens the token dialog where the user can actually type into it", async () => {
    render(Wrapper, { selectedUploader: JUGGLUCO });

    await expect.element(instructions()).toBeVisible();
    await page.getByRole("button", { name: "Generate API key" }).click();

    // Clicking hit-tests: while the instructions are still open over it, the label field
    // belongs to the covered subtree and takes no input.
    await page.getByLabelText("Label").click();
    await expect.element(instructions()).not.toBeInTheDocument();
  });

  it("brings the instructions back when the token dialog closes", async () => {
    render(Wrapper, { selectedUploader: JUGGLUCO });

    await page.getByRole("button", { name: "Generate API key" }).click();
    await page.getByRole("button", { name: "Cancel" }).click();

    await expect.element(instructions()).toBeVisible();
  });

  it("brings them back when the create fails, and keeps the reason", async () => {
    createImpl = () =>
      Promise.reject({ status: 400, body: { message: "Label already used." } });

    render(Wrapper, { selectedUploader: JUGGLUCO });

    await page.getByRole("button", { name: "Generate API key" }).click();
    await tokenDialog().getByRole("button", { name: "Create token" }).click();

    await expect.element(instructions()).toBeVisible();
    await expect.element(page.getByText("Label already used.")).toBeVisible();
  });

  it("leaves the page alone for a token created from the section itself", async () => {
    render(Wrapper, { selectedUploader: JUGGLUCO, open: false });

    await page.getByRole("button", { name: "Create token" }).click();
    await page.getByRole("button", { name: "Cancel" }).click();

    await expect.element(instructions()).not.toBeInTheDocument();
  });
});
