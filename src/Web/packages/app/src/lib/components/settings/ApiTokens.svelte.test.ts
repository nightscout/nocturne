import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

vi.mock("$lib/api/generated/directGrants.generated.remote", () => ({
  list: () => Promise.resolve([]),
  create: () => Promise.resolve({}),
  revoke: () => Promise.resolve(),
}));

import ApiTokens from "./ApiTokens.svelte";

async function openCreateDialog() {
  const onCreateClose = vi.fn();

  render(ApiTokens, { createOpen: true, onCreateClose });

  await expect.element(page.getByText("Create API token")).toBeVisible();
  expect(onCreateClose).not.toHaveBeenCalled();

  return onCreateClose;
}

describe("ApiTokens", () => {
  it("reports the create dialog closing from its own footer", async () => {
    const onCreateClose = await openCreateDialog();

    await page.getByRole("button", { name: "Cancel" }).click();

    expect(onCreateClose).toHaveBeenCalledTimes(1);
  });

  it("reports it closing when the dialog dismisses itself", async () => {
    const onCreateClose = await openCreateDialog();

    await userEvent.keyboard("{Escape}");

    await expect
      .element(page.getByText("Create API token"))
      .not.toBeInTheDocument();
    expect(onCreateClose).toHaveBeenCalledTimes(1);
  });
});
