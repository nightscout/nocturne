import { describe, expect, it } from "vitest";
import {
  buildAppNavigation,
  readOnlyNav,
  type NavItem,
  type NavViewer,
} from "./app-navigation.svelte";

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

describe("readOnlyNav", () => {
  /** The member navigation with every title replaced, as another locale would render it. */
  function retitled(title: (item: NavItem) => string): NavItem[] {
    return buildAppNavigation(MEMBER).map((item) => ({ ...item, title: title(item) }));
  }

  const ids = (items: NavItem[] | null) => items?.map((item) => item.id);

  const RETITLINGS: [string, (item: NavItem) => string][] = [
    ["a translated title", (item) => `Traduit ${item.id}`],
    [
      "a title another entry uses in English",
      (item) => (item.id === "dashboard" ? "Settings" : "Dashboard"),
    ],
    ["an empty title", () => ""],
  ];

  it.each(RETITLINGS)("keeps a guest's entries under %s", (_, title) => {
    expect(ids(readOnlyNav(retitled(title), GUEST))).toEqual([
      "dashboard",
      "calendar",
      "time-spans",
      "reports",
      "clock",
    ]);
  });

  it.each(RETITLINGS)("keeps a public share's entries under %s", (_, title) => {
    expect(ids(readOnlyNav(retitled(title), GLUCOSE_ONLY_SHARE))).toEqual(["dashboard"]);
    expect(ids(readOnlyNav(retitled(title), REPORTING_SHARE))).toEqual([
      "dashboard",
      "reports",
    ]);
  });
});
