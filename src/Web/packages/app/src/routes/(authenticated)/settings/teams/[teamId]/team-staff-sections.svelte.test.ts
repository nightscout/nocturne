import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamDetail, TeamSiteAccess, TeamStaffMember } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  getTeam: vi.fn(),
  updateStaff: vi.fn(),
  removeStaff: vi.fn(),
  createStaffInvite: vi.fn(),
  revokeStaffInvite: vi.fn(),
}));

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getTeam: (id: string) => {
    mocks.getTeam(id);
    return { current: mocks.team, error: undefined, loading: false, ready: true };
  },
  updateStaff: (arg: unknown) => remoteCommand(mocks.updateStaff)(arg),
  removeStaff: (arg: unknown) => remoteCommand(mocks.removeStaff)(arg),
  createStaffInvite: (arg: unknown) => remoteCommand(mocks.createStaffInvite)(arg),
  revokeStaffInvite: (arg: unknown) => remoteCommand(mocks.revokeStaffInvite)(arg),
  createAccessLink: vi.fn(),
  revokeAccessLink: vi.fn(),
  renameTeam: vi.fn(),
  removeManager: vi.fn(),
  createManagerInvite: vi.fn(),
  revokeManagerInvite: vi.fn(),
  deleteTeam: vi.fn(),
}));

vi.mock("$app/navigation", () => ({ goto: vi.fn() }));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const me = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const jo = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
const inviteId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

const manager: TeamDetail = {
  id: teamId,
  name: "Clinic",
  managers: [{ subjectId: me, displayName: "Alex" }],
  managerNames: ["Alex"],
  invites: [],
  staff: [],
  staffInvites: [],
  accessLinks: [],
  sites: [],
  suggestableRoles: [
    { slug: "caretaker", name: "Caretaker", permissions: ["glucose.read", "treatments.readwrite"] },
    { slug: "viewer", name: "Viewer", permissions: ["glucose.read"] },
  ],
  isLastManager: true,
  canDelete: false,
  callerKind: "manager",
};

function staffMember(overrides: Partial<TeamStaffMember> = {}): TeamStaffMember {
  return {
    subjectId: jo,
    displayName: "Jo",
    narrowing: { limitTo24Hours: false },
    hasEnded: false,
    sitesApproved: 0,
    sitesPending: 0,
    ...overrides,
  };
}

function site(overrides: Partial<TeamSiteAccess> = {}): TeamSiteAccess {
  return {
    tenantId: "99999999-9999-9999-9999-999999999999",
    displayName: "Sam's site",
    roleNames: ["Viewer"],
    limitTo24Hours: false,
    givenAt: "2026-11-01T00:00:00Z",
    teamManagesStaff: true,
    pendingStaffCount: 0,
    declinedStaffCount: 0,
    ...overrides,
  };
}

const staffRow = () => page.getByRole("listitem").filter({ hasText: "Jo" });
const editForm = () => page.getByRole("form", { name: "Change the access of Jo" });
const inviteForm = () => page.getByRole("form", { name: "Make a staff link" });
const dialog = () => page.getByRole("alertdialog");

describe("team page staff sections", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: me } };
    mocks.team = manager;
    mocks.getTeam.mockReset();
    mocks.updateStaff.mockReset();
    mocks.removeStaff.mockReset();
    mocks.createStaffInvite.mockReset();
    mocks.revokeStaffInvite.mockReset();
  });

  describe("staff", () => {
    it("says the team has no staff and points to the invite section", async () => {
      render(TeamPage);

      await expect
        .element(page.getByText("This team has no staff. Make a link in Invite staff to add a staff member."))
        .toBeVisible();
      await expect.element(page.getByText("Invite staff", { exact: true })).toBeVisible();
    });

    it.each([
      ["no ceiling", { limitTo24Hours: false }, false, ["Everything the team has"], ["No more than", "Only the last 24 hours", "Access end"]],
      ["a ceiling the API names", { roleCeilingSlug: "viewer", limitTo24Hours: false }, false, ["No more than the role Viewer"], ["Everything the team has"]],
      ["a ceiling no longer offered", { roleCeilingSlug: "clinician", limitTo24Hours: false }, false, ["No more than a role that is no longer offered"], ["clinician"]],
      ["the 24-hour limit", { limitTo24Hours: true }, false, ["Everything the team has", "Only the last 24 hours"], []],
      ["an end to come", { limitTo24Hours: false, endsAt: "2099-02-01T00:00:00Z" }, false, ["Access ends"], ["Access ended"]],
      ["an end that passed", { limitTo24Hours: false, endsAt: "2020-02-01T00:00:00Z" }, true, ["Access ended"], ["Access ends"]],
    ])("words %s", async (_case, narrowing, hasEnded, shown, absent) => {
      mocks.team = { ...manager, staff: [staffMember({ narrowing, hasEnded })] };
      render(TeamPage);

      await expect.element(page.getByText("Jo", { exact: true })).toBeVisible();
      for (const text of shown) await expect.element(staffRow().getByText(text, { exact: false })).toBeVisible();
      for (const text of absent) expect(staffRow().getByText(text, { exact: false }).elements(), text).toHaveLength(0);
    });

    it.each([
      ["approved at one", 1, 0, ["Approved at 1 site."], ["Waits for approval", "Not approved at any site."]],
      ["approved only", 2, 0, ["Approved at 2 sites."], ["Waits for approval", "Not approved at any site."]],
      ["waiting at one", 0, 1, ["Waits for approval at 1 site."], [/Approved at \d/, "Not approved at any site."]],
      ["both", 1, 3, ["Approved at 1 site.", "Waits for approval at 3 sites."], ["Not approved at any site."]],
      ["neither", 0, 0, ["Not approved at any site."], [/Approved at \d/, "Waits for approval"]],
    ])("states where a staff member stands at the sites: %s", async (_case, approved, pending, shown, absent) => {
      mocks.team = { ...manager, staff: [staffMember({ sitesApproved: approved, sitesPending: pending })] };
      render(TeamPage);

      for (const text of shown) await expect.element(staffRow().getByText(text, { exact: false })).toBeVisible();
      for (const text of absent) expect(staffRow().getByText(text, { exact: false }).elements(), String(text)).toHaveLength(0);
      expect(staffRow().getByText("Can see", { exact: false }).elements()).toHaveLength(0);
      expect(staffRow().getByText("declined", { exact: false }).elements()).toHaveLength(0);
    });

    it("shows only that the access ended, and no count, for a staff member whose access ended", async () => {
      mocks.team = {
        ...manager,
        staff: [
          staffMember({
            narrowing: { limitTo24Hours: false, endsAt: "2020-02-01T00:00:00Z" },
            hasEnded: true,
            sitesApproved: 2,
            sitesPending: 1,
          }),
        ],
      };
      render(TeamPage);

      await expect.element(staffRow().getByText("Access ended", { exact: false })).toBeVisible();
      for (const text of ["Approved at", "Waits for approval", "Not approved at any site."]) {
        expect(staffRow().getByText(text, { exact: false }).elements(), text).toHaveLength(0);
      }
    });

    it.each(["Save", "Cancel"])("returns focus to the Edit button of the person after %s closes the form", async (button) => {
      mocks.team = {
        ...manager,
        staff: [staffMember(), staffMember({ subjectId: "dddddddd-dddd-dddd-dddd-dddddddddddd", displayName: "Kim" })],
      };
      mocks.updateStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await editForm().getByRole("button", { name: button }).click();

      await expect.poll(() => editForm().elements().length).toBe(0);
      await expect.element(page.getByRole("button", { name: "Edit Jo" })).toHaveFocus();
    });

    it("edits the narrowing and sends the chosen ceiling, limit and no end, with the sentence about sites that approve", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      mocks.updateStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await expect
        .element(editForm().getByText("If a site approves the team's staff itself, it must approve them again when the change gives them more."))
        .toBeVisible();
      await editForm().getByRole("radio", { name: "Viewer" }).click();
      await editForm().getByLabelText("Only the last 24 hours").click();
      await editForm().getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() =>
        expect(mocks.updateStaff).toHaveBeenCalledWith({
          teamId,
          subjectId: jo,
          request: { roleCeilingSlug: "viewer", limitTo24Hours: true, endsAt: undefined },
        }),
      );
      await expect.poll(() => editForm().elements().length).toBe(0);
    });

    it("sends no ceiling when the manager chooses everything the team has", async () => {
      mocks.team = { ...manager, staff: [staffMember({ narrowing: { roleCeilingSlug: "viewer", limitTo24Hours: true } })] };
      mocks.updateStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await expect.element(editForm().getByRole("radio", { name: "Viewer" })).toBeChecked();
      await editForm().getByRole("radio", { name: "Everything the team has" }).click();
      await editForm().getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() => expect(mocks.updateStaff).toHaveBeenCalled());
      expect(mocks.updateStaff.mock.calls[0]![0].request).toEqual({
        roleCeilingSlug: undefined,
        limitTo24Hours: true,
        endsAt: undefined,
      });
    });

    it("keeps an end that passed exactly as stored when the manager does not change it", async () => {
      const stored = "2020-02-01T05:00:00Z";
      mocks.team = {
        ...manager,
        staff: [staffMember({ narrowing: { limitTo24Hours: false, endsAt: stored }, hasEnded: true })],
      };
      mocks.updateStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await editForm().getByLabelText("Only the last 24 hours").click();
      await expect.element(editForm().getByRole("button", { name: "Save" })).toBeEnabled();
      await editForm().getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() => expect(mocks.updateStaff).toHaveBeenCalled());
      expect(mocks.updateStaff.mock.calls[0]![0].request.endsAt).toBe(stored);
    });

    it("sends a new end as an instant", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      mocks.updateStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await editForm().getByLabelText("End of the staff member's access (optional)").fill("2099-01-31");
      await editForm().getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() => expect(mocks.updateStaff).toHaveBeenCalled());
      expect(mocks.updateStaff.mock.calls[0]![0].request.endsAt).toMatch(/^2099-0[12]-\d{2}T/);
    });

    it("words a refused edit from its code and keeps the form open", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      mocks.updateStaff.mockRejectedValue({
        status: 400,
        body: { message: "English detail", code: "team_staff_end_invalid" },
      });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await editForm().getByRole("button", { name: "Save" }).click();

      await expect
        .element(editForm().getByText("The end date must be in the future. Choose a later date."))
        .toBeVisible();
      expect(page.getByText("English detail").elements()).toHaveLength(0);
    });

    it("words a refused ceiling from its code", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      mocks.updateStaff.mockRejectedValue({
        status: 400,
        body: { message: "English detail", code: "team_staff_ceiling_invalid" },
      });
      render(TeamPage);

      await page.getByRole("button", { name: "Edit Jo" }).click();
      await editForm().getByRole("button", { name: "Save" }).click();

      await expect
        .element(page.getByText("Staff cannot be limited to this role. Choose another role."))
        .toBeVisible();
    });

    it("removes a staff member only after a confirmation that says what they lose", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      mocks.removeStaff.mockResolvedValue({ success: true });
      render(TeamPage);

      await page.getByRole("button", { name: "Remove Jo" }).click();
      await expect
        .element(dialog())
        .toHaveTextContent("Remove Jo from Clinic? They lose what the team gives them at every site.");
      expect(mocks.removeStaff).not.toHaveBeenCalled();

      await dialog().getByRole("button", { name: "Remove staff member" }).click();

      await vi.waitFor(() => expect(mocks.removeStaff).toHaveBeenCalledWith({ teamId, subjectId: jo }));
    });

    it("does not remove a staff member when the confirmation is cancelled", async () => {
      mocks.team = { ...manager, staff: [staffMember()] };
      render(TeamPage);

      await page.getByRole("button", { name: "Remove Jo" }).click();
      await dialog().getByRole("button", { name: "Cancel" }).click();

      await expect.element(dialog()).not.toBeInTheDocument();
      expect(mocks.removeStaff).not.toHaveBeenCalled();
    });
  });

  describe("staff invites", () => {
    it("says what a staff member is", async () => {
      render(TeamPage);

      await expect
        .element(
          page.getByText(
            "A staff member works for the team and sees the sites that the team has access to and that let them in.",
            { exact: false },
          ),
        )
        .toHaveTextContent("A staff member cannot manage the team.");
    });

    it("makes a link with the label, narrowing, uses and expiry, and shows it once", async () => {
      mocks.createStaffInvite.mockResolvedValue({
        id: inviteId,
        token: "secret",
        inviteUrl: "https://example.org/teams/join-staff?token=secret",
        expiresAt: "2026-12-01T00:00:00Z",
      });
      render(TeamPage);

      expect(page.getByRole("button", { name: "Copy the staff link" }).elements()).toHaveLength(0);
      await inviteForm().getByLabelText("Label of the staff link (optional)").fill("  Night nurses ");
      await inviteForm().getByRole("radio", { name: "Caretaker" }).click();
      await inviteForm().getByLabelText("Only the last 24 hours").click();
      await inviteForm().getByLabelText("10 people").click();
      await inviteForm().getByLabelText("30 days").click();
      await inviteForm().getByRole("button", { name: "Make staff link" }).click();

      await vi.waitFor(() =>
        expect(mocks.createStaffInvite).toHaveBeenCalledWith({
          teamId,
          request: {
            label: "Night nurses",
            roleCeilingSlug: "caretaker",
            limitTo24Hours: true,
            staffEndsAt: undefined,
            expiresInDays: 30,
            maxUses: 10,
          },
        }),
      );
      await expect
        .element(page.getByLabelText("Staff link", { exact: true }))
        .toHaveValue("https://example.org/teams/join-staff?token=secret");
      await expect.element(page.getByRole("button", { name: "Copy the staff link" })).toBeVisible();
      await expect.element(inviteForm().getByLabelText("Label of the staff link (optional)")).toHaveValue("");
    });

    it("sends no ceiling, one use and the end as an instant by default and when chosen", async () => {
      mocks.createStaffInvite.mockResolvedValue({ id: inviteId, inviteUrl: "https://example.org/x" });
      render(TeamPage);

      await inviteForm().getByLabelText("End of the staff member's access (optional)").fill("2099-01-31");
      await inviteForm().getByRole("button", { name: "Make staff link" }).click();

      await vi.waitFor(() => expect(mocks.createStaffInvite).toHaveBeenCalled());
      const request = mocks.createStaffInvite.mock.calls[0]![0].request;
      expect(request).toMatchObject({ roleCeilingSlug: undefined, limitTo24Hours: false, expiresInDays: 7, maxUses: 1 });
      expect(request.staffEndsAt).toMatch(/^2099-0[12]-\d{2}T/);
    });

    it("offers 1, 10 and 50 uses only, and sends the number chosen", async () => {
      mocks.createStaffInvite.mockResolvedValue({ id: inviteId, inviteUrl: "https://example.org/x" });
      render(TeamPage);

      const uses = inviteForm().getByRole("radiogroup", { name: "Number of people who can use the link" });
      await expect.element(uses).toBeVisible();
      expect(uses.getByRole("radio").elements()).toHaveLength(3);

      for (const [option, sent] of [
        ["1 person", 1],
        ["10 people", 10],
        ["50 people", 50],
      ] as const) {
        mocks.createStaffInvite.mockClear();
        await uses.getByRole("radio", { name: option }).click();
        await inviteForm().getByRole("button", { name: "Make staff link" }).click();
        await vi.waitFor(() => expect(mocks.createStaffInvite).toHaveBeenCalledTimes(1));
        expect(mocks.createStaffInvite.mock.calls[0]![0].request.maxUses, option).toBe(sent);
      }
    });

    it("names each expiry group on the page", async () => {
      render(TeamPage);

      const groups = page.getByRole("radiogroup", { name: "The link stops working after" });
      await expect.element(groups.first()).toBeVisible();
      expect(groups.elements()).toHaveLength(3);
      await expect
        .element(inviteForm().getByRole("radiogroup", { name: "The link stops working after" }))
        .toBeVisible();
      await expect
        .element(
          page
            .getByRole("form", { name: "Make an access link" })
            .getByRole("radiogroup", { name: "The link stops working after" }),
        )
        .toBeVisible();
    });

    it("words a refused link from its code and shows no link", async () => {
      mocks.createStaffInvite.mockRejectedValue({
        status: 400,
        body: { message: "English detail", code: "team_staff_end_invalid" },
      });
      render(TeamPage);

      await inviteForm().getByRole("button", { name: "Make staff link" }).click();

      await expect
        .element(inviteForm().getByText("The end date must be in the future. Choose a later date."))
        .toBeVisible();
      expect(page.getByText("English detail").elements()).toHaveLength(0);
      expect(page.getByRole("button", { name: "Copy the staff link" }).elements()).toHaveLength(0);
    });

    it("says no staff link works when the API sends none", async () => {
      render(TeamPage);

      await expect.element(page.getByText("No staff link works now.")).toBeVisible();
    });

    it.each([
      ["no ceiling and no cap", { limitTo24Hours: false }, undefined, 2, ["Everything the team has", "Uses: 2, no limit"]],
      ["a ceiling, the limit and an end", { roleCeilingSlug: "viewer", limitTo24Hours: true, endsAt: "2099-02-01T00:00:00Z" }, 5, 1, ["No more than the role Viewer", "Only the last 24 hours", "Access ends", "Uses: 1 of 5"]],
    ])("lists a live link with %s, and revokes it", async (_case, narrowing, maxUses, useCount, shown) => {
      mocks.team = {
        ...manager,
        staffInvites: [
          {
            id: inviteId,
            label: "Night nurses",
            expiresAt: "2026-12-01T00:00:00Z",
            maxUses,
            useCount,
            createdBySubjectId: me,
            createdByName: "Alex",
            narrowing,
          },
        ],
      };
      mocks.revokeStaffInvite.mockResolvedValue({ success: true });
      render(TeamPage);

      const row = page.getByRole("listitem").filter({ hasText: "Night nurses" });
      for (const text of shown) await expect.element(row.getByText(text, { exact: false })).toBeVisible();
      await expect.element(row.getByText("Made by Alex", { exact: false })).toBeVisible();
      expect(page.getByText("No staff link works now.").elements()).toHaveLength(0);

      await page.getByRole("button", { name: "Revoke the staff link Night nurses" }).click();

      await vi.waitFor(() => expect(mocks.revokeStaffInvite).toHaveBeenCalledWith({ teamId, inviteId }));
    });

    it("clears the shown link when that link is revoked", async () => {
      mocks.createStaffInvite.mockResolvedValue({ id: inviteId, inviteUrl: "https://example.org/x" });
      mocks.team = {
        ...manager,
        staffInvites: [
          {
            id: inviteId,
            label: "Night nurses",
            expiresAt: "2026-12-01T00:00:00Z",
            maxUses: 1,
            useCount: 0,
            createdBySubjectId: me,
            createdByName: "Alex",
            narrowing: { limitTo24Hours: false },
          },
        ],
      };
      mocks.revokeStaffInvite.mockResolvedValue({ success: true });
      render(TeamPage);

      await inviteForm().getByRole("button", { name: "Make staff link" }).click();
      await expect.element(page.getByLabelText("Staff link", { exact: true })).toBeVisible();

      await page.getByRole("button", { name: "Revoke the staff link Night nurses" }).click();

      await vi.waitFor(() => expect(mocks.revokeStaffInvite).toHaveBeenCalled());
      await expect.poll(() => page.getByLabelText("Staff link", { exact: true }).elements().length).toBe(0);
    });
  });

  describe("sites", () => {
    it.each([
      [true, "The team decides which of its staff can see this site.", "An owner of the site must approve each staff member."],
      [false, "An owner of the site must approve each staff member.", "The team decides which of its staff can see this site."],
    ])("words the switch when the team manages its staff is %s", async (teamManagesStaff, shown, absent) => {
      mocks.team = { ...manager, sites: [site({ teamManagesStaff })] };
      render(TeamPage);

      await expect.element(page.getByText(shown)).toBeVisible();
      expect(page.getByText(absent).elements()).toHaveLength(0);
    });

    it("says that declines still hold when the team decides and the site declined staff", async () => {
      mocks.team = { ...manager, sites: [site({ teamManagesStaff: true, declinedStaffCount: 2 })] };
      render(TeamPage);

      await expect
        .element(
          page.getByText(
            "The team decides which of its staff can see this site. Staff that the site declined stay declined.",
          ),
        )
        .toBeVisible();
      await expect.element(page.getByText("The site declined 2 staff members.")).toBeVisible();
    });

    it("states no switch and no count for a site whose access ended", async () => {
      mocks.team = {
        ...manager,
        sites: [
          site({
            accessEndsAt: "2020-02-01T00:00:00Z",
            hasEnded: true,
            teamManagesStaff: true,
            pendingStaffCount: 2,
            declinedStaffCount: 1,
          }),
        ],
      };
      render(TeamPage);

      await expect.element(page.getByText("Access ended", { exact: false })).toBeVisible();
      for (const text of [
        "The team decides which of its staff",
        "An owner of the site must approve each staff member.",
        "wait for the site's approval",
        "The site declined",
      ]) {
        expect(page.getByText(text, { exact: false }).elements(), text).toHaveLength(0);
      }
    });

    it("says nothing about waiting or declined staff when both counts are zero", async () => {
      mocks.team = { ...manager, sites: [site()] };
      render(TeamPage);

      await expect.element(page.getByText("Sam's site")).toBeVisible();
      expect(page.getByText("wait for the site's approval", { exact: false }).elements()).toHaveLength(0);
      expect(page.getByText("waits for the site's approval", { exact: false }).elements()).toHaveLength(0);
      expect(page.getByText("The site declined", { exact: false }).elements()).toHaveLength(0);
    });

    it.each([
      [1, 1, "1 staff member waits for the site's approval.", "The site declined 1 staff member."],
      [2, 3, "2 staff members wait for the site's approval.", "The site declined 3 staff members."],
    ])("states %i waiting and %i declined", async (pending, declined, pendingText, declinedText) => {
      mocks.team = {
        ...manager,
        sites: [site({ teamManagesStaff: false, pendingStaffCount: pending, declinedStaffCount: declined })],
      };
      render(TeamPage);

      await expect.element(page.getByText(pendingText)).toBeVisible();
      await expect.element(page.getByText(declinedText)).toBeVisible();
    });

    it("states that only an owner of the site changes the switch, and offers no control for it", async () => {
      mocks.team = { ...manager, sites: [site()] };
      render(TeamPage);

      await expect
        .element(page.getByText("Only an owner of the site can change this.", { exact: false }))
        .toBeVisible();
      expect(page.getByRole("switch").elements()).toHaveLength(0);
    });
  });
});
