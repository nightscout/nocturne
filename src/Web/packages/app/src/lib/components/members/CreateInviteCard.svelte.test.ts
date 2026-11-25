import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";
import { getLocalTimeZone, today } from "@internationalized/date";
import { RoleAccessLevel, type TenantRoleDto } from "@nocturne/api-client";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { createInvite } from "#lib/api/generated/memberInvites.generated.remote.js";
import { accessEndDate, accessEndInstant } from "./access-end.js";
import CreateInviteCard from "./CreateInviteCard.svelte";

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  createInvite: vi.fn(),
}));

function dayFromToday(days: number): string {
  return today(getLocalTimeZone()).add({ days }).toString();
}

const accessEndLabel = "Access ends (optional)";

const ownerRoleId = "11111111-1111-1111-1111-111111111111";
const caretakerRoleId = "22222222-2222-2222-2222-222222222222";
const deniedRoleId = "55555555-5555-5555-5555-555555555555";

/** `GET /roles` as an Administrator reads it: every role, Owner not grantable. */
const administratorRoles: TenantRoleDto[] = [
  {
    id: ownerRoleId,
    name: "Owner",
    slug: "owner",
    permissions: ["*"],
    accessLevel: RoleAccessLevel.Manage,
    canGrant: false,
  },
  {
    id: caretakerRoleId,
    name: "Caretaker",
    slug: "caretaker",
    permissions: ["glucose.read"],
    accessLevel: RoleAccessLevel.ReadOnly,
    canGrant: true,
  },
];

describe("CreateInviteCard", () => {
  it("offers only the roles the inviter can grant", async () => {
    render(CreateInviteCard, { roles: administratorRoles, onCancel: () => {} });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });

  it("does not offer a role that gives no access", async () => {
    render(CreateInviteCard, {
      roles: [
        ...administratorRoles,
        { id: deniedRoleId, name: "Denied", slug: "denied", permissions: [], canGrant: true },
      ],
      onCancel: () => {},
    });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Denied" }).elements()).toHaveLength(0);
  });

  it("offers only the access levels whose role the inviter can grant", async () => {
    render(CreateInviteCard, {
      roles: administratorRoles,
      roleChoices: [
        { roleId: ownerRoleId, label: "Can do everything", description: "Owner" },
        { roleId: caretakerRoleId, label: "Can see readings", description: "Caretaker" },
      ],
      onCancel: () => {},
    });

    await expect.element(page.getByText("Can see readings")).toBeVisible();
    expect(page.getByText("Can do everything").elements()).toHaveLength(0);
  });
});

describe("CreateInviteCard access end", () => {
  const allRoles: TenantRoleDto[] = [
    { ...administratorRoles[0], canGrant: true },
    administratorRoles[1],
  ];

  beforeEach(() => {
    vi.mocked(createInvite).mockReset();
    vi.mocked(createInvite).mockResolvedValue({ inviteUrl: "/join?token=abc" });
  });

  async function create() {
    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Create link" }).click();
  }

  it("sends the instant the picked day ends", async () => {
    render(CreateInviteCard, { roles: allRoles, onCancel: () => {} });

    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await create();

    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
    expect(vi.mocked(createInvite).mock.calls[0][0].accessEndsAt).toBe(accessEndInstant("2099-07-14"));
  });

  it("sends no end when the field is empty", async () => {
    render(CreateInviteCard, { roles: allRoles, onCancel: () => {} });

    await expect.element(page.getByLabelText(accessEndLabel)).toBeVisible();
    await create();

    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
    expect(vi.mocked(createInvite).mock.calls[0][0].accessEndsAt).toBeUndefined();
  });

  it("offers today as the earliest day", async () => {
    render(CreateInviteCard, { roles: allRoles, onCancel: () => {} });

    await expect.element(page.getByLabelText(accessEndLabel)).toHaveAttribute("min", dayFromToday(0));
  });

  const ceilingLine = (last: string) =>
    `Your own access ends on ${formatMediumDate(accessEndDate(accessEndInstant(last)))}. The access you give ends then, or on an earlier day that you choose.`;

  it("limits the day to the inviter's own last day, leaves the field empty and states what that means", async () => {
    const last = dayFromToday(30);
    render(CreateInviteCard, {
      roles: allRoles,
      callerAccessEndsAt: accessEndInstant(last),
      onCancel: () => {},
    });

    const field = page.getByLabelText(accessEndLabel);
    await expect.element(field).toHaveAttribute("max", last);
    await expect.element(field).toHaveValue("");
    await expect.element(page.getByText(ceilingLine(last))).toBeVisible();
    await expect.element(field).toHaveAttribute("aria-describedby", "invite-access-end-help");
    await expect.element(page.getByText(ceilingLine(last))).toHaveAttribute("id", "invite-access-end-help");
  });

  it("sends no end for an empty field under a ceiling, which gives the inviter's own end", async () => {
    render(CreateInviteCard, {
      roles: allRoles,
      callerAccessEndsAt: accessEndInstant(dayFromToday(30)),
      onCancel: () => {},
    });

    await create();

    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
    expect(vi.mocked(createInvite).mock.calls[0][0].accessEndsAt).toBeUndefined();
  });

  it("leaves a cleared field empty under a ceiling and still creates", async () => {
    const last = dayFromToday(30);
    render(CreateInviteCard, {
      roles: allRoles,
      callerAccessEndsAt: accessEndInstant(last),
      onCancel: () => {},
    });

    const field = page.getByLabelText(accessEndLabel);
    await field.fill(dayFromToday(5));
    await field.fill("");

    await expect.element(field).toHaveValue("");
    await create();
    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
  });

  it("still offers today and creates on the inviter's last day when the end is at midnight", async () => {
    const last = dayFromToday(0);
    render(CreateInviteCard, {
      roles: allRoles,
      callerAccessEndsAt: accessEndInstant(last),
      onCancel: () => {},
    });

    await expect.element(page.getByLabelText(accessEndLabel)).toHaveAttribute("max", last);
    await create();
    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
  });

  describe("when the inviter's end is mid-day today", () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ["Date"] });
      vi.setSystemTime(new Date(2099, 0, 10, 12, 0));
    });
    afterEach(() => vi.useRealTimers());

    it("disables the field, states the end as the day it falls in, and still creates", async () => {
      const ceiling = new Date(2099, 0, 10, 18, 0).toISOString();
      render(CreateInviteCard, { roles: allRoles, callerAccessEndsAt: ceiling, onCancel: () => {} });

      await expect.element(page.getByLabelText(accessEndLabel)).toBeDisabled();
      await expect
        .element(
          page.getByText(
            `Your own access ends on ${formatMediumDate(new Date(2099, 0, 10))}. The access you give ends then, or on an earlier day that you choose.`,
          ),
        )
        .toBeVisible();
      await create();
      await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
      expect(vi.mocked(createInvite).mock.calls[0][0].accessEndsAt).toBeUndefined();
    });
  });

  it("does not create an invite for a day past the inviter's own", async () => {
    render(CreateInviteCard, {
      roles: allRoles,
      callerAccessEndsAt: accessEndInstant(dayFromToday(30)),
      onCancel: () => {},
    });

    await page.getByLabelText(accessEndLabel).fill(dayFromToday(31));
    await page.getByRole("checkbox", { name: "Caretaker" }).click();

    await expect.element(page.getByRole("button", { name: "Create link" })).toBeDisabled();
  });

  it("removes the field and its value when the owner role is chosen", async () => {
    render(CreateInviteCard, { roles: allRoles, onCancel: () => {} });

    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await page.getByRole("checkbox", { name: "Owner" }).click();

    await expect.element(page.getByText("An owner's access does not end.")).toBeVisible();
    expect(page.getByLabelText(accessEndLabel).elements()).toHaveLength(0);
    await page.getByRole("button", { name: "Create link" }).click();
    await expect.poll(() => vi.mocked(createInvite).mock.calls.length).toBe(1);
    expect(vi.mocked(createInvite).mock.calls[0][0].accessEndsAt).toBeUndefined();
  });
});
