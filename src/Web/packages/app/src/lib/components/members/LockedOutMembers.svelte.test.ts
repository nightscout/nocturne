import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { RecoveryIssueRefusal, type LockedOutMember } from "@nocturne/api-client";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

let members: LockedOutMember[];
let issueImpl: (subjectId: string) => Promise<unknown>;

vi.mock("$app/state", () => ({
  page: { url: new URL("https://acme.nocturne.example/auth/recovery") },
}));

vi.mock("#lib/api/generated/memberRecoveries.generated.remote.js", () => ({
  getLockedOutMembers: () => remoteQuery(() => members),
  issueRecoveryCode: (subjectId: string) => issueImpl(subjectId),
}));

import LockedOutMembers from "./LockedOutMembers.svelte";

const orphan: LockedOutMember = {
  subjectId: "11111111-1111-1111-1111-111111111111",
  name: "Alex",
  username: "alex",
  hasRecoveryCodes: false,
};

describe("LockedOutMembers", () => {
  beforeEach(() => {
    members = [orphan];
    issueImpl = async () => ({ code: "code+with/chars", expiresAt: new Date() });
  });

  it("creates a recovery link on this host carrying the issued code", async () => {
    const issued: string[] = [];
    issueImpl = async (subjectId) => {
      issued.push(subjectId);
      return { code: "code+with/chars", expiresAt: new Date() };
    };
    render(LockedOutMembers);

    await page.getByRole("button", { name: "Create link" }).click();

    await expect
      .element(page.getByRole("textbox", { name: "Recovery link" }))
      .toHaveValue("https://acme.nocturne.example/auth/recovery?code=code%2Bwith%2Fchars");
    expect(issued).toEqual([orphan.subjectId]);
  });

  it("leaves a member only the operator can recover without a button", async () => {
    members = [{ ...orphan, refusal: RecoveryIssueRefusal.OperatorOnly, hasRecoveryCodes: true }];
    render(LockedOutMembers);

    await expect
      .element(page.getByText("Only the operator can recover this account"))
      .toBeInTheDocument();
    expect(page.getByRole("button", { name: "Create link" }).elements()).toHaveLength(0);
    expect(page.getByText("They still have recovery codes", { exact: false }).elements()).toHaveLength(0);
  });

  it("tells the administrator a member still has recovery codes of their own", async () => {
    members = [{ ...orphan, hasRecoveryCodes: true }];
    render(LockedOutMembers);

    await expect
      .element(page.getByText("They still have recovery codes", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Create link" })).toBeInTheDocument();
  });

  it("shows why a link could not be created", async () => {
    issueImpl = async () => error(409, "Only the operator can issue this code.");
    render(LockedOutMembers);

    await page.getByRole("button", { name: "Create link" }).click();

    await expect
      .element(page.getByText("Only the operator can issue this code."))
      .toBeInTheDocument();
    expect(page.getByRole("textbox", { name: "Recovery link" }).elements()).toHaveLength(0);
  });

  it("renders nothing when nobody is locked out", async () => {
    members = [];
    render(LockedOutMembers);

    expect(page.getByText("Members who cannot sign in").elements()).toHaveLength(0);
  });

  it("lists the members but gives the link to a sign-in on a handed-off session", async () => {
    render(LockedOutMembers, { signInRequired: true });

    await expect.element(page.getByText("Alex", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Sign in again to make a recovery link.")).toBeVisible();
    expect(page.getByRole("button", { name: "Create link" }).elements()).toHaveLength(0);
  });

  it.each([
    [RecoveryIssueRefusal.MissingPermissions, "This member holds permissions that you do not hold."],
    [RecoveryIssueRefusal.HistoryClamped, "You can see the last 24 hours only."],
    [RecoveryIssueRefusal.AccessOutlasts, "This member's access lasts longer than yours."],
  ])("offers no Create link and says who can help when the server refuses with %s", async (refusal, reason) => {
    members = [{ ...orphan, refusal }];

    render(LockedOutMembers);

    await expect.element(page.getByText(reason, { exact: false })).toBeVisible();
    await expect.element(page.getByText("Ask an owner to create the link.", { exact: false })).toBeVisible();
    expect(page.getByRole("button", { name: "Create link" }).elements()).toHaveLength(0);
  });
});
