import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { getLocalTimeZone, today } from "@internationalized/date";
import type { MembershipRequestDto, TenantRoleDto } from "@nocturne/api-client";
import { accessEndInstant } from "./access-end.js";
import PendingRequestsList from "./PendingRequestsList.svelte";

/** `GET /roles` as an Administrator reads it: every role, Owner not grantable. */
const administratorRoles: TenantRoleDto[] = [
  { id: "11111111-1111-1111-1111-111111111111", name: "Owner", slug: "owner", permissions: ["*"], canGrant: false },
  {
    id: "22222222-2222-2222-2222-222222222222",
    name: "Caretaker",
    slug: "caretaker",
    permissions: ["glucose.read"],
    canGrant: true,
  },
];

const requests: MembershipRequestDto[] = [
  { id: "33333333-3333-3333-3333-333333333333", subjectName: "Sam" },
];

describe("PendingRequestsList", () => {
  it("offers only the roles the approver can grant", async () => {
    render(PendingRequestsList, {
      requests,
      roles: administratorRoles,
      onApprove: async () => {},
      onDeny: async () => {},
    });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });
});

describe("PendingRequestsList access end", () => {
  const roles = administratorRoles.map((r) => ({ ...r, canGrant: true }));
  const accessEndLabel = "Access ends (optional)";

  function props(onApprove = vi.fn().mockResolvedValue(undefined), extra = {}) {
    return { requests, roles, onApprove, onDeny: async () => {}, ...extra };
  }

  it("approves with the instant the picked day ends", async () => {
    const onApprove = vi.fn().mockResolvedValue(undefined);
    render(PendingRequestsList, props(onApprove));

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await page.getByRole("button", { name: "Approve" }).click();

    expect(onApprove).toHaveBeenCalledWith(requests[0].id, [roles[1].id], accessEndInstant("2099-07-14"));
  });

  it("approves with no end when the field is empty", async () => {
    const onApprove = vi.fn().mockResolvedValue(undefined);
    render(PendingRequestsList, props(onApprove));

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Approve" }).click();

    expect(onApprove).toHaveBeenCalledWith(requests[0].id, [roles[1].id], undefined);
  });

  it("removes the field and its value when the owner role is chosen", async () => {
    const onApprove = vi.fn().mockResolvedValue(undefined);
    render(PendingRequestsList, props(onApprove));

    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await page.getByRole("checkbox", { name: "Owner" }).click();

    await expect.element(page.getByText("An owner's access does not end.")).toBeVisible();
    expect(page.getByLabelText(accessEndLabel).elements()).toHaveLength(0);
    await page.getByRole("button", { name: "Approve" }).click();
    expect(onApprove).toHaveBeenCalledWith(requests[0].id, [roles[0].id], undefined);
  });

  it("limits the day to the approver's own, leaves the field empty and approves with no date", async () => {
    const last = today(getLocalTimeZone()).add({ days: 30 }).toString();
    render(PendingRequestsList, props(undefined, { callerAccessEndsAt: accessEndInstant(last) }));

    const field = page.getByLabelText(accessEndLabel);
    await expect.element(field).toHaveAttribute("max", last);
    await expect.element(field).toHaveValue("");
    await expect
      .element(page.getByText("The access you give ends then, or on an earlier day that you choose.", { exact: false }))
      .toBeVisible();
    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await expect.element(page.getByRole("button", { name: "Approve" })).toBeEnabled();
  });

  it("approves with no date on the approver's last day", async () => {
    const onApprove = vi.fn().mockResolvedValue(undefined);
    const last = today(getLocalTimeZone()).toString();
    render(PendingRequestsList, props(onApprove, { callerAccessEndsAt: accessEndInstant(last) }));

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Approve" }).click();

    expect(onApprove).toHaveBeenCalledWith(requests[0].id, [roles[1].id], undefined);
  });

  it("does not overwrite what is typed under a ceiling", async () => {
    const last = today(getLocalTimeZone()).add({ days: 30 }).toString();
    render(PendingRequestsList, props(undefined, { callerAccessEndsAt: accessEndInstant(last) }));

    const field = page.getByLabelText(accessEndLabel);
    await field.fill(today(getLocalTimeZone()).add({ days: 5 }).toString());
    await field.fill("");

    await expect.element(field).toHaveValue("");
  });
});
