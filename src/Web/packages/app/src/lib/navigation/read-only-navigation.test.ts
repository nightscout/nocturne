import { describe, expect, it } from "vitest";
import { readOnlyNav, type NavViewer } from "./read-only-navigation";

/** The sidebar's navigation, in the order AppSidebar builds it. */
const SIDEBAR_NAV = [
  { title: "Dashboard" },
  { title: "Calendar" },
  { title: "Time Spans" },
  { title: "Reports" },
  { title: "Clock" },
  { title: "Tenants" },
  { title: "Food" },
  { title: "Meals" },
  { title: "Tools" },
  { title: "Alerts" },
  { title: "Dev Tools" },
  { title: "Settings" },
];

const MEMBER: NavViewer = {
  isGuestSession: false,
  anonymous: false,
  grantedScopes: ["*"],
};
/** A share on its default categories. */
const GLUCOSE_ONLY_SHARE: NavViewer = {
  isGuestSession: false,
  anonymous: true,
  grantedScopes: ["glucose.read"],
};
const REPORTING_SHARE: NavViewer = {
  ...GLUCOSE_ONLY_SHARE,
  grantedScopes: ["glucose.read", "reports.read"],
};
const GUEST: NavViewer = {
  isGuestSession: true,
  anonymous: false,
  grantedScopes: ["glucose.read", "reports.read"],
};

function titles(viewer: NavViewer): string[] | null {
  return readOnlyNav(SIDEBAR_NAV, viewer)?.map((item) => item.title) ?? null;
}

describe("readOnlyNav", () => {
  it("leaves a member's navigation alone", () => {
    expect(titles(MEMBER)).toBeNull();
  });

  it("offers a public share only the dashboard when it grants no reports", () => {
    expect(titles(GLUCOSE_ONLY_SHARE)).toEqual(["Dashboard"]);
  });

  it("adds reports to a public share that grants them", () => {
    expect(titles(REPORTING_SHARE)).toEqual(["Dashboard", "Reports"]);
  });

  it("withholds every owner surface from a public share", () => {
    const visible = titles(REPORTING_SHARE) ?? [];

    for (const owned of [
      "Food",
      "Meals",
      "Tools",
      "Alerts",
      "Dev Tools",
      "Settings",
      "Tenants",
    ]) {
      expect(visible).not.toContain(owned);
    }
  });

  it("counts a readwrite grant as its read counterpart", () => {
    expect(
      titles({ ...GLUCOSE_ONLY_SHARE, grantedScopes: ["reports.readwrite"] })
    ).toContain("Reports");
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
});
