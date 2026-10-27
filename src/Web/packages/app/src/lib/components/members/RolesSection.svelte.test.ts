import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import type { TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

let roles: TenantRoleDto[];
let createImpl: (arg: unknown) => Promise<unknown>;
let updateImpl: (arg: unknown) => Promise<unknown>;

vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => remoteQuery(() => roles),
  createRole: (arg: unknown) => createImpl(arg),
  updateRole: (arg: unknown) => updateImpl(arg),
  deleteRole: () => Promise.resolve(),
}));

import RolesSection from "./RolesSection.svelte";

const REFUSAL = "'statistics.read' is not a known permission.";

const caretaker: TenantRoleDto = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Caretaker",
  slug: "caretaker",
  isSystem: false,
  permissions: ["glucose.read"],
  memberCount: 0,
};

async function openEditAndRename(name: string) {
  render(RolesSection);
  await page.getByRole("button", { name: "Edit" }).click();
  const field = page.getByRole("dialog").getByLabelText("Name");
  await field.clear();
  await field.fill(name);
}

describe("RolesSection", () => {
  beforeEach(() => {
    roles = [caretaker];
    createImpl = () => Promise.resolve({});
    updateImpl = () => Promise.resolve({});
  });

  it("offers no Statistics row, and says Reports covers statistics", async () => {
    render(RolesSection);
    await page.getByRole("button", { name: "Create role" }).click();

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("Reports", { exact: true })).toBeVisible();
    await expect
      .element(dialog.getByText(/Generated reports and data exports, and statistics/))
      .toBeVisible();
    expect(dialog.getByText("Statistics", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows a refused edit inside the dialog and keeps the dialog open", async () => {
    updateImpl = async () => error(400, REFUSAL);

    await openEditAndRename("Carer");
    await page.getByRole("button", { name: "Save" }).click();

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByRole("alert")).toHaveTextContent(REFUSAL);
    await expect.element(dialog.getByRole("button", { name: "Save" })).toBeVisible();
  });

  it("clears a refused edit's message when the dialog is opened again", async () => {
    updateImpl = async () => error(400, REFUSAL);

    await openEditAndRename("Carer");
    await page.getByRole("button", { name: "Save" }).click();
    await expect.element(page.getByRole("dialog").getByRole("alert")).toBeVisible();

    await page.getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    await page.getByRole("button", { name: "Edit" }).click();

    await expect.element(page.getByRole("dialog")).toBeVisible();
    expect(page.getByRole("dialog").getByRole("alert").elements()).toHaveLength(0);
  });

  it("closes the dialog and reports success when the edit saves", async () => {
    let sent: unknown;
    updateImpl = async (arg) => {
      sent = arg;
      return {};
    };

    await openEditAndRename("Carer");
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Role updated successfully.")).toBeVisible();
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    expect(sent).toMatchObject({
      id: caretaker.id,
      request: { name: "Carer", permissions: ["glucose.read"] },
    });
  });

  it("shows a refused create inside the create dialog", async () => {
    createImpl = async () => error(400, REFUSAL);

    render(RolesSection);
    await page.getByRole("button", { name: "Create role" }).click();
    await page.getByRole("dialog").getByLabelText("Name").fill("Viewer");
    await page.getByRole("button", { name: "Create", exact: true }).click();

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByRole("alert")).toHaveTextContent(REFUSAL);
  });

  it("does not carry a refused create's message into the next create", async () => {
    createImpl = async () => error(400, REFUSAL);

    render(RolesSection);
    await page.getByRole("button", { name: "Create role" }).click();
    await page.getByRole("dialog").getByLabelText("Name").fill("Viewer");
    await page.getByRole("button", { name: "Create", exact: true }).click();
    await expect.element(page.getByRole("dialog").getByRole("alert")).toBeVisible();

    await page.getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    await page.getByRole("button", { name: "Create role" }).click();

    await expect.element(page.getByRole("dialog")).toBeVisible();
    expect(page.getByRole("dialog").getByRole("alert").elements()).toHaveLength(0);
  });
});
