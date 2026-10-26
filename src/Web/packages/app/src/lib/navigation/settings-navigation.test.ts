import { describe, expect, it } from "vitest";
import {
  visibleSettingsGroups,
  type SettingsViewer,
} from "./settings-navigation.svelte";

const OWNER: SettingsViewer = { grantedScopes: ["*"], isPlatformAdmin: false };

function hrefs(viewer: SettingsViewer): Record<string, string[]> {
  return Object.fromEntries(
    visibleSettingsGroups(viewer).map((group) => [group.id, group.links.map((l) => l.href)])
  );
}

describe("visibleSettingsGroups", () => {
  it("gives an owner the personal and site groups, without the platform group", () => {
    const groups = hrefs(OWNER);

    expect(Object.keys(groups)).toEqual(["personal", "site"]);
    expect(groups.site).toContain("/setup");
    expect(groups.site).toContain("/settings/site");
    expect(groups.site).toContain("/settings/audit");
  });

  it("adds the platform group for a platform admin", () => {
    expect(hrefs({ ...OWNER, isPlatformAdmin: true }).platform).toEqual([
      "/settings/admin",
      "/settings/admin/tenants",
      "/settings/access-requests",
      "/settings/admin/connector-cursors",
    ]);
  });

  it("offers a platform admin who holds no site scope the platform group still", () => {
    expect(Object.keys(hrefs({ grantedScopes: [], isPlatformAdmin: true }))).toEqual([
      "personal",
      "platform",
    ]);
  });

  it("offers each site page only to a member holding one of its scopes", () => {
    const groups = hrefs({ grantedScopes: ["therapy.read", "members.invite"], isPlatformAdmin: false });

    expect(groups.site).toEqual([
      "/settings/patient",
      "/settings/profile",
      "/settings/members",
    ]);
  });

  it("satisfies a read scope with its readwrite counterpart", () => {
    expect(hrefs({ grantedScopes: ["therapy.readwrite"], isPlatformAdmin: false }).site).toEqual([
      "/settings/patient",
      "/settings/profile",
    ]);
  });

  it("drops the site group when the member holds none of its scopes", () => {
    expect(Object.keys(hrefs({ grantedScopes: ["glucose.read"], isPlatformAdmin: false }))).toEqual([
      "personal",
    ]);
  });

  it("does not offer the help pages as settings", () => {
    const all = Object.values(hrefs({ ...OWNER, isPlatformAdmin: true })).flat();

    expect(all).not.toContain("/settings/support");
    expect(all).not.toContain("/settings/translations");
  });
});
