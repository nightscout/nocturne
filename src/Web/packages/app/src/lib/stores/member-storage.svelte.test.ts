import { afterEach, describe, expect, it } from "vitest";
import { SIGNED_OUT_COOKIE, clearIfSignedOut, clearMemberStorage, memberKey, memberOwner } from "./member-storage.js";

afterEach(() => {
  localStorage.clear();
  document.cookie = `${SIGNED_OUT_COOKIE}=; path=/; max-age=0`;
});

describe("the owner of what the browser keeps", () => {
  it("is the tenant and the member's subject", () => {
    const owner = memberOwner({ tenantSlug: "dev", user: { subjectId: "subject-1" } });
    expect(owner).toEqual({ tenant: "dev", subject: "subject-1" });
    expect(memberKey(owner, "picks")).toBe("nocturne.member.picks.dev.subject-1");
  });

  it("is nobody for a guest session, a page with no user, or no tenant", () => {
    for (const data of [
      { tenantSlug: "dev", user: { subjectId: "guest" }, isGuestSession: true },
      { tenantSlug: "dev" },
      { user: { subjectId: "subject-1" } },
    ]) {
      expect(memberKey(memberOwner(data), "picks"), JSON.stringify(data)).toBeNull();
    }
  });
});

describe("clearing what the browser keeps for members", () => {
  it("removes every member key and leaves the rest", () => {
    localStorage.setItem("nocturne.member.custom-report-draft.dev.subject-1.new", "{}");
    localStorage.setItem("nocturne.member.report-signal-picks.dev.subject-2", "{}");
    localStorage.setItem("mode-watcher-mode", "dark");

    clearMemberStorage();

    expect(localStorage.length).toBe(1);
    expect(localStorage.getItem("mode-watcher-mode")).toBe("dark");
  });

  it("happens on load only after the sign-out endpoint marked the browser, and once", () => {
    localStorage.setItem("nocturne.member.report-signal-picks.dev.subject-1", "{}");

    clearIfSignedOut();
    expect(localStorage.length).toBe(1);

    document.cookie = `${SIGNED_OUT_COOKIE}=1; path=/`;
    clearIfSignedOut();
    expect(localStorage.length).toBe(0);
    expect(document.cookie).not.toContain(SIGNED_OUT_COOKIE);
  });
});
