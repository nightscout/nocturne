import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { TenantRoleDto, TenantTeamAccess } from "@nocturne/api-client";
import { EditTeamAccessRequestSchema } from "@nocturne/api-client/schemas";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { accessEndDate, accessEndInstant } from "./access-end.js";
import TeamAccessEditForm from "./TeamAccessEditForm.svelte";

const viewer: TenantRoleDto = {
  id: "0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b",
  name: "Viewer",
  slug: "viewer",
  permissions: ["glucose.read"],
  canGrant: true,
  canGiveToTeam: true,
};
const caretaker: TenantRoleDto = {
  id: "0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c",
  name: "Caretaker",
  slug: "caretaker",
  permissions: ["glucose.read", "treatments.readwrite"],
  canGrant: true,
  canGiveToTeam: true,
};
const owner: TenantRoleDto = {
  id: "33333333-3333-3333-3333-333333333333",
  name: "Owner",
  slug: "owner",
  permissions: ["*"],
  canGrant: true,
  canGiveToTeam: false,
};
const beyondCaller: TenantRoleDto = {
  id: "44444444-4444-4444-4444-444444444444",
  name: "Night nurse",
  slug: "night-nurse",
  permissions: ["glucose.read", "insulin.readwrite"],
  canGrant: false,
  canGiveToTeam: false,
};

const team: TenantTeamAccess = {
  teamId: "55555555-5555-5555-5555-555555555555",
  teamName: "Clinic",
  roles: [{ id: viewer.id, name: "Viewer", slug: "viewer" }],
  limitTo24Hours: false,
  hasEnded: false,
  staff: [],
};

function show(
  overrides: Partial<{ team: TenantTeamAccess; roles: TenantRoleDto[]; limitForced: boolean; callerAccessEndsAt: string }> = {},
) {
  const onSave = vi.fn();
  render(TeamAccessEditForm, {
    team,
    roles: [viewer, caretaker, owner, beyondCaller],
    limitForced: false,
    saving: false,
    onSave,
    onCancel: () => {},
    ...overrides,
  });
  return { onSave };
}

describe("TeamAccessEditForm roles", () => {
  it("offers only the roles the API says the caller may give a team, with the team's roles chosen", async () => {
    show();

    await expect.element(page.getByRole("checkbox", { name: "Viewer" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).not.toBeChecked();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
    expect(page.getByRole("checkbox", { name: "Night nurse" }).elements()).toHaveLength(0);
  });

  it("says which of the team's roles the caller cannot give, and that a save takes them away", async () => {
    show({ team: { ...team, roles: [...(team.roles ?? []), { id: beyondCaller.id, name: "Night nurse", slug: "night-nurse" }] } });

    await expect
      .element(page.getByText("The team has roles that you cannot give: Night nurse. If you save, the team loses them."))
      .toBeVisible();
  });

  it("offers no control, with the reason, when the caller may give a team no role", async () => {
    show({ roles: [owner, beyondCaller] });

    await expect
      .element(page.getByText("You cannot give a team any role of this site, so you cannot change this access."))
      .toBeVisible();
    expect(page.getByRole("button", { name: "Save" }).elements()).toHaveLength(0);
  });

  it("disables Save, with the reason, when no role is chosen", async () => {
    show();

    await page.getByRole("checkbox", { name: "Viewer" }).click();

    await expect.element(page.getByText("Choose at least one role.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("says that a change applies to every staff member at once", async () => {
    show();

    await expect
      .element(
        page.getByText(
          "The change applies to all staff of the team at once. Less access stops at once, and more access never makes approved staff wait again.",
        ),
      )
      .toBeVisible();
  });
});

describe("TeamAccessEditForm 24-hour limit", () => {
  it("keeps the limit on and disabled for a caller who reads only the last 24 hours, with the reason", async () => {
    const { onSave } = show({ limitForced: true });

    const limit = page.getByRole("checkbox", { name: "Only the last 24 hours" });
    await expect.element(limit).toBeChecked();
    await expect.element(limit).toBeDisabled();
    await expect
      .element(limit)
      .toHaveAccessibleDescription(
        "You can see only the last 24 hours, so the access that you give must keep this limit.",
      );

    await page.getByRole("button", { name: "Save" }).click();

    expect(onSave.mock.calls[0]![0].limitTo24Hours).toBe(true);
  });

  it("lets a caller who is not limited lift the team's limit", async () => {
    const { onSave } = show({ team: { ...team, limitTo24Hours: true } });

    const limit = page.getByRole("checkbox", { name: "Only the last 24 hours" });
    await expect.element(limit).toBeChecked();
    await limit.click();
    await page.getByRole("button", { name: "Save" }).click();

    expect(onSave.mock.calls[0]![0].limitTo24Hours).toBe(false);
  });
});

describe("TeamAccessEditForm end", () => {
  it("prefills an end that has not passed", async () => {
    show({ team: { ...team, accessEndsAt: accessEndInstant("2099-07-14") } });

    await expect.element(page.getByLabelText("Access ends (optional)")).toHaveValue("2099-07-14");
  });

  it("does not prefill an end that has passed, and says that a new end or none starts the access again", async () => {
    const passed = accessEndInstant("2020-01-31");
    const { onSave } = show({ team: { ...team, accessEndsAt: passed, hasEnded: true } });

    await expect.element(page.getByLabelText("Access ends (optional)")).toHaveValue("");
    await expect
      .element(
        page.getByText(
          `This access ended on ${formatMediumDate(accessEndDate(passed))}. A new end date, or no end date, starts the access again for the approved staff.`,
        ),
      )
      .toBeVisible();

    await page.getByRole("button", { name: "Save" }).click();

    expect(onSave.mock.calls[0]![0].accessEndsAt).toBeNull();
  });

  it("requires an end no later than the caller's own", async () => {
    show({ callerAccessEndsAt: accessEndInstant("2099-07-14") });

    const end = page.getByLabelText("Access ends", { exact: true });
    await expect.element(end).toHaveAttribute("max", "2099-07-14");
    await expect.element(page.getByRole("button", { name: "Save" })).toBeDisabled();
  });
});

describe("TeamAccessEditForm sending", () => {
  it("sends every field, with null for no end", async () => {
    const { onSave } = show();

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    expect(onSave).toHaveBeenCalledWith({
      roleIds: [viewer.id, caretaker.id],
      limitTo24Hours: false,
      accessEndsAt: null,
    });
  });

  it("sends a body with no end that the schema of the remote function keeps the roles and the limit of", async () => {
    const { onSave } = show();

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    const body = JSON.parse(JSON.stringify(EditTeamAccessRequestSchema.parse(onSave.mock.calls[0][0])));
    expect(body).toEqual({ roleIds: [viewer.id, caretaker.id], limitTo24Hours: false });
  });

  it("sends a body with an end that the schema of the remote function keeps the end of", async () => {
    const { onSave } = show();

    await page.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await page.getByRole("button", { name: "Save" }).click();

    const body = JSON.parse(JSON.stringify(EditTeamAccessRequestSchema.parse(onSave.mock.calls[0][0])));
    expect(body).toEqual({
      roleIds: [viewer.id],
      limitTo24Hours: false,
      accessEndsAt: accessEndInstant("2099-07-14"),
    });
  });

  it("sends the instant the picked day ends", async () => {
    const { onSave } = show();

    await page.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await page.getByRole("button", { name: "Save" }).click();

    expect(onSave).toHaveBeenCalledWith({
      roleIds: [viewer.id],
      limitTo24Hours: false,
      accessEndsAt: accessEndInstant("2099-07-14"),
    });
  });

  it("disables Save while the edit runs", async () => {
    const onSave = vi.fn();
    render(TeamAccessEditForm, {
      team,
      roles: [viewer],
      limitForced: false,
      saving: true,
      onSave,
      onCancel: () => {},
    });

    await expect.element(page.getByRole("button", { name: "Save" })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Cancel" })).toBeDisabled();
  });
});
