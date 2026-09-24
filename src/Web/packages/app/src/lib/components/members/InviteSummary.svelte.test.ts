import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { JoinInviteInfo } from "$lib/api/generated/nocturne-api-client";
import InviteSummary from "./InviteSummary.svelte";

const viewerInvite: JoinInviteInfo = {
  tenantName: "Sam's Diabetes",
  createdByName: "Alex Carer",
  roleNames: ["Viewer"],
  permissions: ["glucose.read", "reports.read"],
  grantsAccess: true,
  isViewOnlyForRecordsAndAccess: true,
  limitTo24Hours: false,
};

describe("InviteSummary", () => {
  it("names the inviter and the site by their display names", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect.element(page.getByText("Alex Carer")).toBeVisible();
    await expect
      .element(page.getByText("Sam's Diabetes").first())
      .toBeVisible();
  });

  it("describes the role and what it gives access to", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect
      .element(page.getByText("Viewer", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("Blood Glucose", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("Reports", { exact: true }))
      .toBeVisible();
    await expect
      .element(
        page.getByText(
          /can't change records, treatment\s+settings or who has access/
        )
      )
      .toBeVisible();
  });

  it("does not claim a role that can make changes is look-only", async () => {
    render(InviteSummary, {
      props: {
        invite: {
          ...viewerInvite,
          roleNames: ["Caretaker"],
          permissions: ["glucose.read", "treatments.readwrite"],
          isViewOnlyForRecordsAndAccess: false,
        },
        signedIn: false,
      },
    });

    await expect
      .element(page.getByText("Caretaker", { exact: true }))
      .toBeVisible();
    await expect
      .element(
        page.getByText(
          /can't change records, treatment\s+settings or who has access/
        )
      )
      .not.toBeInTheDocument();
  });

  it("says only the last 24 hours are visible when the invite is limited", async () => {
    render(InviteSummary, {
      props: {
        invite: { ...viewerInvite, limitTo24Hours: true },
        signedIn: false,
      },
    });

    await expect
      .element(page.getByText(/only see the last 24 hours/))
      .toBeVisible();
  });

  it("leaves out the 24-hour note when the invite shows all history", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect
      .element(page.getByText(/only see the last 24 hours/))
      .not.toBeInTheDocument();
  });

  it("tells a signed-out visitor to choose a sign-in method next", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect
      .element(page.getByText(/choose how you'd like to sign in/))
      .toBeVisible();
  });

  it("tells a signed-in visitor to accept next", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: true } });

    await expect
      .element(page.getByText(/accept the invite below/))
      .toBeVisible();
  });

  it("says a viewer can acknowledge alerts, and what that does", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect
      .element(page.getByText(/press Acknowledge on an alert/))
      .toBeVisible();
    await expect
      .element(
        page.getByText(/stops\s+the alert being passed on to other people/)
      )
      .toBeVisible();
  });

  it("does not promise alerts beyond an open Nocturne", async () => {
    render(InviteSummary, { props: { invite: viewerInvite, signedIn: false } });

    await expect
      .element(
        page.getByText(/While you have Nocturne open, you'll see alerts/)
      )
      .toBeVisible();
    await expect
      .element(page.getByText(/doesn't send you alerts/))
      .not.toBeInTheDocument();
  });

  it("tells the invitee to ask for a new link when the invite grants nothing", async () => {
    render(InviteSummary, {
      props: {
        invite: {
          ...viewerInvite,
          roleNames: [],
          permissions: [],
          grantsAccess: false,
          isViewOnlyForRecordsAndAccess: false,
        },
        signedIn: false,
      },
    });

    await expect
      .element(page.getByText(/no longer gives access to anything/))
      .toBeVisible();
    await expect
      .element(page.getByText(/Alex Carer\s+for a new invite link/))
      .toBeVisible();
    await expect
      .element(page.getByText(/choose how you'd like to sign in/))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByText(/What you'll have access to/))
      .not.toBeInTheDocument();
  });
});
