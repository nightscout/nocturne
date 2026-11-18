import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";

let createImpl: (body?: unknown) => Promise<{ id?: string; token?: string }>;

vi.mock("#lib/api/generated/directGrants.generated.remote.js", () => ({
  create: (body: unknown) => createImpl(body),
}));

import ApiTokenCreateDialog from "./ApiTokenCreateDialog.svelte";

const prefilled = {
  open: true,
  prefillLabel: "Follower phone",
  prefillScopes: ["glucose.read"],
};

const submit = () =>
  page.getByRole("dialog").getByRole("button", { name: "Create token" }).click();

beforeEach(() => {
  createImpl = () => Promise.resolve({ id: "grant-1", token: "noc_created" });
});

describe("ApiTokenCreateDialog", () => {
  it("sends the 24-hour limit the creator ticked", async () => {
    const create = vi.fn((_body?: unknown) =>
      Promise.resolve({ id: "grant-1", token: "noc_created" })
    );
    createImpl = create;
    render(ApiTokenCreateDialog, prefilled);

    await page.getByLabelText("Only last 24 hours").click();
    await submit();

    await expect.element(page.getByText("Token created")).toBeVisible();
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        label: "Follower phone",
        scopes: ["glucose.read"],
        limitTo24Hours: true,
      })
    );
  });

  it("shows the token with the base URL an app also needs", async () => {
    const onCreated = vi.fn();
    render(ApiTokenCreateDialog, {
      ...prefilled,
      baseUrl: "https://example.test",
      onCreated,
    });

    await submit();

    await expect
      .element(page.getByRole("textbox", { name: "API token" }))
      .toHaveValue("noc_created");
    await expect
      .element(page.getByRole("textbox", { name: "Base URL" }))
      .toHaveValue("https://example.test");
    await expect
      .element(page.getByRole("button", { name: "Copy base URL" }))
      .toBeVisible();
    expect(onCreated).toHaveBeenCalledWith("grant-1");
  });

  it("keeps the form open with the reason when the create fails", async () => {
    createImpl = async () => error(409, "Label already used.");
    render(ApiTokenCreateDialog, prefilled);

    await submit();

    await expect.element(page.getByText("Label already used.")).toBeVisible();
    await expect
      .element(page.getByRole("textbox", { name: "Label" }))
      .toHaveValue("Follower phone");
  });
});
