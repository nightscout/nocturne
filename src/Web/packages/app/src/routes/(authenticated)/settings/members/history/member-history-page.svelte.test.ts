import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import {
  MemberHistoryActorKind,
  MemberHistoryKind,
  type MemberHistoryEntry,
} from "@nocturne/api-client";
import { remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({
  getMemberHistory: vi.fn(),
  getMembers: vi.fn(),
  getRoles: vi.fn(),
}));

const nav = vi.hoisted(() => ({
  url: new URL("http://localhost/settings/members/history"),
  data: {} as { effectivePermissions?: string[] },
  notify: () => {},
}));

vi.mock("$app/state", async () => {
  const { createSubscriber } = await import("svelte/reactivity");
  const subscribe = createSubscriber((update) => {
    nav.notify = update;
    return () => {};
  });
  return {
    page: {
      get url() {
        subscribe();
        return nav.url;
      },
      data: nav.data,
    },
  };
});

function goTo(url: string) {
  nav.url = new URL(url);
  nav.notify();
}

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  getMemberHistory: (params: unknown) => mocks.getMemberHistory(params),
  getMembers: () => mocks.getMembers(),
}));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => mocks.getRoles(),
}));

import HistoryPage from "./+page.svelte";

const memberId = "44444444-4444-4444-4444-444444444444";

function entries(count: number, first = 0): MemberHistoryEntry[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `e${first + i}`,
    at: "2026-03-01T09:00:00Z",
    kind: MemberHistoryKind.RoleGiven,
    actor: { kind: MemberHistoryActorKind.Person, subjectId: "a1", name: "Alex" },
    member: { subjectId: memberId, name: "Sam" },
    role: `role-${first + i}`,
    viaInvite: false,
  }));
}

function pageOf(data: MemberHistoryEntry[], offset: number, total: number) {
  return remoteQuery(() => ({ data, pagination: { limit: 25, offset, total } }));
}

beforeEach(() => {
  vi.clearAllMocks();
  nav.data.effectivePermissions = ["members.manage"];
  nav.url = new URL("http://localhost/settings/members/history");
  mocks.getMembers.mockReturnValue(
    remoteQuery(() => [{ id: "m1", subjectId: memberId, name: "Sam" }]),
  );
  mocks.getRoles.mockReturnValue(remoteQuery(() => []));
});

describe("member history page", () => {
  it("lists the changes newest first as the API returns them", async () => {
    mocks.getMemberHistory.mockReturnValue(pageOf(entries(2), 0, 2));
    render(HistoryPage);

    await expect.element(browserPage.getByRole("heading", { name: "Member history" })).toBeVisible();
    const rows = browserPage.getByRole("listitem");
    await expect.element(rows.first()).toHaveTextContent("Alex gave Sam the role role-0.");
    await expect.element(rows.last()).toHaveTextContent("Alex gave Sam the role role-1.");
    expect(mocks.getMemberHistory).toHaveBeenCalledWith({
      memberSubjectId: undefined,
      limit: 25,
      offset: 0,
    });
  });

  it("states the member and offers all changes when filtered to one member", async () => {
    nav.url = new URL(`http://localhost/settings/members/history?member=${memberId}`);
    mocks.getMemberHistory.mockReturnValue(pageOf(entries(1), 0, 1));
    render(HistoryPage);

    await expect.element(browserPage.getByText("Changes to Sam")).toBeVisible();
    await expect.element(browserPage.getByRole("link", { name: "Show all changes" })).toBeVisible();
    expect(mocks.getMemberHistory).toHaveBeenCalledWith({
      memberSubjectId: memberId,
      limit: 25,
      offset: 0,
    });
  });

  it("offers no way back to all changes when it is not filtered", async () => {
    mocks.getMemberHistory.mockReturnValue(pageOf(entries(1), 0, 1));
    render(HistoryPage);

    await expect.element(browserPage.getByRole("listitem")).toBeVisible();
    expect(browserPage.getByRole("link", { name: "Show all changes" }).elements()).toHaveLength(0);
  });

  it("pages 25 at a time", async () => {
    mocks.getMemberHistory.mockImplementation(({ offset }: { offset: number }) =>
      offset === 0 ? pageOf(entries(25), 0, 30) : pageOf(entries(5, 25), 25, 30),
    );
    render(HistoryPage);

    await expect.element(browserPage.getByText("1–25 of 30")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Newer" })).toBeDisabled();
    await browserPage.getByRole("button", { name: "Older" }).click();

    await expect.element(browserPage.getByText("26–30 of 30")).toBeVisible();
    expect(mocks.getMemberHistory).toHaveBeenLastCalledWith({
      memberSubjectId: undefined,
      limit: 25,
      offset: 25,
    });
    await expect.element(browserPage.getByRole("button", { name: "Older" })).toBeDisabled();
    await browserPage.getByRole("button", { name: "Newer" }).click();
    await expect.element(browserPage.getByText("1–25 of 30")).toBeVisible();
  });

  it("states there is no access, and sends no read, without members.manage", async () => {
    nav.data.effectivePermissions = ["audit.read", "members.invite"];
    render(HistoryPage);

    await expect
      .element(browserPage.getByText("You do not have access to the member history."))
      .toBeVisible();
    expect(mocks.getMemberHistory).not.toHaveBeenCalled();
    expect(mocks.getMembers).not.toHaveBeenCalled();
    expect(mocks.getRoles).not.toHaveBeenCalled();
  });

  it("states that no changes are recorded", async () => {
    mocks.getMemberHistory.mockReturnValue(pageOf([], 0, 0));
    render(HistoryPage);

    await expect.element(browserPage.getByText("No member changes are recorded.")).toBeVisible();
  });

  it("states a failed read and offers to try again", async () => {
    mocks.getMemberHistory.mockReturnValue(remoteQueryError(new Error("failed")));
    render(HistoryPage);

    await expect.element(browserPage.getByText("The member history did not load.")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Try again" })).toBeVisible();
    expect(browserPage.getByText("No member changes are recorded.").elements()).toHaveLength(0);
  });

  it("treats a member value that is not a GUID as no filter and never sends it", async () => {
    nav.url = new URL("http://localhost/settings/members/history?member=not-a-guid");
    mocks.getMemberHistory.mockReturnValue(pageOf(entries(1), 0, 1));
    render(HistoryPage);

    await expect.element(browserPage.getByRole("listitem")).toBeVisible();
    expect(mocks.getMemberHistory).toHaveBeenCalledWith({
      memberSubjectId: undefined,
      limit: 25,
      offset: 0,
    });
    expect(browserPage.getByRole("link", { name: "Show all changes" }).elements()).toHaveLength(0);
  });

  it("heads a GUID that no member list or entry names with a neutral name", async () => {
    nav.url = new URL("http://localhost/settings/members/history?member=99999999-9999-9999-9999-999999999999");
    mocks.getMemberHistory.mockReturnValue(pageOf([], 0, 0));
    render(HistoryPage);

    await expect.element(browserPage.getByText("Changes to one member")).toBeVisible();
    expect(browserPage.getByText(/deleted member/).elements()).toHaveLength(0);
  });

  it("heads a member an entry proves existed and that has no name as a deleted member", async () => {
    nav.url = new URL(`http://localhost/settings/members/history?member=${memberId}`);
    mocks.getMembers.mockReturnValue(remoteQuery(() => []));
    mocks.getMemberHistory.mockReturnValue(
      pageOf([{ ...entries(1)[0], member: { subjectId: memberId } }], 0, 1),
    );
    render(HistoryPage);

    await expect.element(browserPage.getByText("Changes to a deleted member")).toBeVisible();
  });

  it("sends exactly one read, from the first page, when the filter changes on a later page", async () => {
    mocks.getMemberHistory.mockImplementation(({ offset }: { offset: number }) =>
      pageOf(entries(25, offset), offset, 60),
    );
    render(HistoryPage);
    await expect.element(browserPage.getByText("1–25 of 60")).toBeVisible();
    await browserPage.getByRole("button", { name: "Older" }).click();
    await expect.element(browserPage.getByText("26–50 of 60")).toBeVisible();

    mocks.getMemberHistory.mockClear();
    goTo(`http://localhost/settings/members/history?member=${memberId}`);

    await expect.element(browserPage.getByText("Changes to Sam")).toBeVisible();
    await expect.element(browserPage.getByText("1–25 of 60")).toBeVisible();
    expect(mocks.getMemberHistory.mock.calls).toEqual([
      [{ memberSubjectId: memberId, limit: 25, offset: 0 }],
    ]);
  });
});
