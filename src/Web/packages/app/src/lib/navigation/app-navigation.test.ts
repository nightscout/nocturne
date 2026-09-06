import { describe, expect, it } from "vitest";
import { buildAppNavigation, type NavViewer } from "./app-navigation";

const MEMBER: NavViewer = {
  user: { subjectId: "s1" },
  isGuestSession: false,
  isPlatformAdmin: false,
  grantedScopes: ["*"],
  tenantCount: 1,
  tenantless: false,
};
/** A share link on its default categories. */
const GLUCOSE_ONLY_SHARE: NavViewer = {
  ...MEMBER,
  user: null,
  grantedScopes: ["glucose.read"],
};
const REPORTING_SHARE: NavViewer = {
  ...GLUCOSE_ONLY_SHARE,
  grantedScopes: ["glucose.read", "reports.read"],
};
const GUEST: NavViewer = {
  ...MEMBER,
  isGuestSession: true,
  grantedScopes: ["glucose.read", "reports.read"],
};

/** Surfaces only the data owner can act on. */
const OWNER_ONLY = [
  "Food",
  "Meals",
  "Tools",
  "Alerts",
  "Dev Tools",
  "Settings",
];

function titles(viewer: NavViewer): string[] {
  return buildAppNavigation(viewer).map((item) => item.title);
}

describe("buildAppNavigation", () => {
  it("gives a member every surface", () => {
    const visible = titles(MEMBER);

    for (const owned of OWNER_ONLY) {
      expect(visible).toContain(owned);
    }
    expect(visible).toContain("Dashboard");
    expect(visible).toContain("Reports");
  });

  it("offers a public share only the dashboard when it grants no reports", () => {
    expect(titles(GLUCOSE_ONLY_SHARE)).toEqual(["Dashboard"]);
  });

  it("adds reports to a public share that grants them", () => {
    expect(titles(REPORTING_SHARE)).toEqual(["Dashboard", "Reports"]);
  });

  it("withholds every owner surface from a public share", () => {
    const visible = titles(REPORTING_SHARE);

    for (const owned of [...OWNER_ONLY, "Tenants"]) {
      expect(visible).not.toContain(owned);
    }
  });

  it("offers a share nothing beyond the dashboard when its scopes are unknown", () => {
    expect(titles({ ...GLUCOSE_ONLY_SHARE, grantedScopes: [] })).toEqual([
      "Dashboard",
    ]);
  });

  it("keeps the guest link's read-only navigation", () => {
    expect(titles(GUEST)).toEqual([
      "Dashboard",
      "Calendar",
      "Time Spans",
      "Reports",
      "Clock",
    ]);
  });

  it("does not narrow a guest link to the share's surfaces", () => {
    expect(titles({ ...GUEST, grantedScopes: ["glucose.read"] })).toContain(
      "Reports"
    );
  });

  it("offers the tenant switcher only to a member holding more than one", () => {
    expect(titles({ ...MEMBER, tenantCount: 2 })).toContain("Tenants");
    expect(titles({ ...REPORTING_SHARE, tenantCount: 2 })).not.toContain(
      "Tenants"
    );
  });
});
