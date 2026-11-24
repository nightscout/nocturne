import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import type { TenantRoleDto } from "@nocturne/api-client";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

let roles: TenantRoleDto[];
let createImpl: (arg: unknown) => Promise<unknown>;
let updateImpl: (arg: unknown) => Promise<unknown>;
let deleteImpl: (arg: unknown) => Promise<unknown>;

vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => remoteQuery(() => roles),
  createRole: (arg: unknown) => createImpl(arg),
  updateRole: (arg: unknown) => updateImpl(arg),
  deleteRole: (arg: unknown) => deleteImpl(arg),
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
    deleteImpl = () => Promise.resolve();
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

  it("shows the translated sentence for a role name that is taken, not the server's text", async () => {
    createImpl = async () => error(409, "A role with this name already exists.", { code: "role_name_taken" });

    render(RolesSection);
    await page.getByRole("button", { name: "Create role" }).click();
    await page.getByRole("dialog").getByLabelText("Name").fill("Caretaker");
    await page.getByRole("button", { name: "Create", exact: true }).click();

    await expect
      .element(page.getByRole("dialog").getByRole("alert"))
      .toHaveTextContent("A role with this name already exists. Use a different name.");
  });

  it("shows the translated sentence for a rename to a taken name", async () => {
    updateImpl = async () => error(409, "A role with this name already exists.", { code: "role_name_taken" });

    await openEditAndRename("Owner");
    await page.getByRole("button", { name: "Save" }).click();

    await expect
      .element(page.getByRole("dialog").getByRole("alert"))
      .toHaveTextContent("A role with this name already exists. Use a different name.");
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

  it("shows a refused delete inside the confirm dialog and keeps it there", async () => {
    deleteImpl = async () => error(400, REFUSAL);

    render(RolesSection);
    await page.getByRole("button").nth(2).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toBeVisible();
    await dialog.getByRole("button", { name: "Delete" }).click();

    await expect.element(dialog.getByRole("alert")).toHaveTextContent(REFUSAL);
    expect(page.getByRole("alert").elements()).toHaveLength(1);

    await new Promise((resolve) => setTimeout(resolve, 3500));
    await expect.element(dialog.getByRole("alert")).toHaveTextContent(REFUSAL);
  });

  it("does not carry a refused delete's message into the next delete", async () => {
    deleteImpl = async () => error(400, REFUSAL);

    render(RolesSection);
    await page.getByRole("button").nth(2).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Delete" }).click();
    await expect.element(page.getByRole("alertdialog").getByRole("alert")).toBeVisible();

    await page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
    await page.getByRole("button").nth(2).click();

    await expect.element(page.getByRole("alertdialog")).toBeVisible();
    expect(page.getByRole("alertdialog").getByRole("alert").elements()).toHaveLength(0);
  });

  it("offers only View and says why for a role a longer-lasting member holds", async () => {
    roles = [{ ...caretaker, outlastsCaller: true, memberCount: 1 }];

    render(RolesSection);

    const reason = "A member whose access lasts longer than yours has this role.";
    await expect.element(page.getByText(reason)).toBeVisible();
    await expect
      .element(page.getByRole("group", { name: "Caretaker" }))
      .toHaveAccessibleDescription(reason);
    expect(page.getByRole("button", { name: "Edit" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Delete role" }).elements()).toHaveLength(0);

    await page.getByRole("button", { name: "View" }).click();
    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("View role")).toBeVisible();
    await expect
      .element(dialog.getByText("A member whose access lasts longer than yours has this role."))
      .toBeVisible();
    expect(dialog.getByText(/System roles are managed/).elements()).toHaveLength(0);
    expect(dialog.getByRole("button", { name: "Save" }).elements()).toHaveLength(0);
  });

  it("offers only View and states the invite when a longer-lasting invite uses the role", async () => {
    roles = [{ ...caretaker, inviteOutlastsCaller: true }];

    render(RolesSection);

    const reason = "An invite that gives access longer than yours uses this role.";
    await expect.element(page.getByText(reason)).toBeVisible();
    await expect
      .element(page.getByRole("group", { name: "Caretaker" }))
      .toHaveAccessibleDescription(reason);
    expect(page.getByRole("button", { name: "Edit" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Delete role" }).elements()).toHaveLength(0);
    await expect.element(page.getByRole("button", { name: "View" })).toBeVisible();
  });

  it("offers edit and delete and no reason for a role within reach", async () => {
    roles = [{ ...caretaker, outlastsCaller: false }];

    render(RolesSection);

    await expect.element(page.getByRole("button", { name: "Edit" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Delete role" })).toBeVisible();
    expect(
      page.getByText("A member whose access lasts longer than yours has this role.").elements(),
    ).toHaveLength(0);
  });
});

describe("RolesSection roles a team holds", () => {
  const held: TenantRoleDto = { ...caretaker, heldByTeam: true };

  beforeEach(() => {
    createImpl = () => Promise.resolve({});
    updateImpl = () => Promise.resolve({});
    deleteImpl = () => Promise.resolve();
  });

  it("says a role is held by a team and disables its delete with the reason", async () => {
    roles = [held];
    render(RolesSection);

    await expect.element(page.getByText("Held by a team")).toBeVisible();
    await expect
      .element(
        page.getByText("A team has this role. It cannot be deleted, and it can hold only permissions for data."),
      )
      .toBeVisible();
    const remove = page.getByRole("button", { name: "Delete role" });
    await expect.element(remove).toBeDisabled();
    await expect.element(remove).toHaveAccessibleDescription(/A team has this role/);
    await expect.element(page.getByRole("button", { name: "Edit" })).toBeEnabled();
  });

  it("leaves delete enabled, and says nothing about a team, for a role no team holds", async () => {
    roles = [caretaker];
    render(RolesSection);

    await expect.element(page.getByRole("button", { name: "Delete role" })).toBeEnabled();
    expect(page.getByText("Held by a team").elements()).toHaveLength(0);
    expect(page.getByText("A team has this role", { exact: false }).elements()).toHaveLength(0);
  });

  it("tells a caller who is not an owner that only an owner can add permissions", async () => {
    roles = [{ ...held, onlyOwnersMayWiden: true }];
    render(RolesSection);

    await expect.element(page.getByText("Only an owner can add permissions to it.")).toBeVisible();
    await page.getByRole("button", { name: "Edit" }).click();
    await expect
      .element(page.getByRole("dialog"))
      .toHaveTextContent("You can remove permissions from it, and only an owner can add permissions.");
  });

  it("does not say only an owner can add permissions to an owner", async () => {
    roles = [held];
    render(RolesSection);

    await expect.element(page.getByText("Held by a team")).toBeVisible();
    expect(page.getByText("Only an owner can add permissions", { exact: false }).elements()).toHaveLength(0);
  });

  it.each([
    ["role_held_by_team", "A team has this role, so it can hold only permissions for data and you cannot delete it."],
    ["role_held_by_team_owner_only", "A team has this role, so only an owner can add permissions to it."],
  ])("words a refused save with the code %s", async (code, sentence) => {
    roles = [held];
    updateImpl = async () => {
      throw { status: 409, body: { message: "English detail", code } };
    };
    await openEditAndRename("Carer");
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByRole("dialog").getByRole("alert")).toHaveTextContent(sentence);
    expect(page.getByText("English detail").elements()).toHaveLength(0);
  });
});

describe("RolesSection what a caller may choose for a role a team holds", () => {
  const option = (name: string) => page.getByRole("option", { name, exact: true });

  it("lets a caller who is not an owner choose Read for a role that holds read and write", async () => {
    roles = [
      {
        ...caretaker,
        permissions: ["treatments.read", "treatments.readwrite"],
        heldByTeam: true,
        onlyOwnersMayWiden: true,
        coveredPermissions: ["treatments.read", "treatments.readwrite"],
      },
    ];
    render(RolesSection);
    await page.getByRole("button", { name: "Edit" }).click();

    await page.getByRole("dialog").getByText("Read & write", { exact: true }).first().click();

    await expect.element(option("Read")).not.toHaveAttribute("data-disabled");
    await expect.element(option("No access")).not.toHaveAttribute("data-disabled");
    await expect.element(option("Read & write")).not.toHaveAttribute("data-disabled");
  });

  it("does not let a caller who is not an owner add a permission the role lacks", async () => {
    roles = [
      {
        ...caretaker,
        permissions: ["glucose.read"],
        heldByTeam: true,
        onlyOwnersMayWiden: true,
        coveredPermissions: ["glucose.read"],
      },
    ];
    render(RolesSection);
    await page.getByRole("button", { name: "Edit" }).click();

    await page.getByRole("dialog").getByText("Read", { exact: true }).first().click();

    await expect.element(option("Read & write")).toHaveAttribute("data-disabled");
    await expect.element(option("No access")).not.toHaveAttribute("data-disabled");
  });

  it("does not let an owner choose a level the API refuses for a role a team holds, before any save", async () => {
    roles = [
      {
        ...caretaker,
        permissions: ["glucose.read"],
        heldByTeam: true,
        teamRefusedPermissions: ["glucose.readwrite"],
      },
    ];
    render(RolesSection);
    await page.getByRole("button", { name: "Edit" }).click();

    await page.getByRole("dialog").getByText("Read", { exact: true }).first().click();

    await expect.element(option("Read & write")).toHaveAttribute("data-disabled");
    await expect.element(option("Read")).not.toHaveAttribute("data-disabled");
  });
});

