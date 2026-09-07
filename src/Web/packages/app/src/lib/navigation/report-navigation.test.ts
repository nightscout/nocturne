import { describe, it, expect } from "vitest";
import {
  reportCategories,
  visibleReportCategories,
  getSidebarReportItems,
  type ReportViewer,
} from "./report-navigation";

/** Seed-role grants, as `MemberScopeMiddleware` resolves them. */
const OWNER = ["*"];
const VIEWER = [
  "glucose.read",
  "reports.read",
  "device.notify",
  "device.actuate",
];
/** A share link on its default categories. */
const GLUCOSE_ONLY_SHARE = ["glucose.read"];

function hrefs(viewer: ReportViewer): string[] {
  return visibleReportCategories(viewer)
    .flatMap((c) => c.reports)
    .map((r) => r.href);
}

const everyHref = reportCategories.flatMap((c) => c.reports).map((r) => r.href);

describe("visibleReportCategories", () => {
  it("offers every report to a full-scope member", () => {
    expect(hrefs({ grantedScopes: OWNER, anonymous: false })).toEqual(
      everyHref
    );
  });

  it("drops the reports a Viewer cannot load", () => {
    const visible = hrefs({ grantedScopes: VIEWER, anonymous: false });

    expect(visible).toContain("/reports/executive-summary");
    expect(visible).toContain("/reports/agp");
    expect(visible).toContain("/reports/comparison");
    // treatments.read
    expect(visible).not.toContain("/reports/treatments");
    expect(visible).not.toContain("/reports/day-in-review");
    expect(visible).not.toContain("/reports/idp");
    // devices.read
    expect(visible).not.toContain("/reports/battery");
    expect(visible).not.toContain("/reports/site-change-impact");
    // stepcount.read / heartrate.read / sleep.read
    expect(visible).not.toContain("/reports/steps");
    expect(visible).not.toContain("/reports/heart-rate");
    expect(visible).not.toContain("/reports/sleep");
  });

  it("offers a glucose-only share only the reports glucose alone can render", () => {
    const visible = hrefs({
      grantedScopes: GLUCOSE_ONLY_SHARE,
      anonymous: true,
    });

    expect(visible).toEqual([
      "/reports/year-overview",
      "/reports/readings",
      "/reports/month-to-month",
    ]);
  });

  it("still withholds member-only reports from a share holding their scopes", () => {
    const shareScopes = ["glucose.read", "treatments.read", "reports.read"];

    expect(hrefs({ grantedScopes: shareScopes, anonymous: false })).toContain(
      "/reports/idp"
    );
    expect(
      hrefs({ grantedScopes: shareScopes, anonymous: true })
    ).not.toContain("/reports/idp");
  });

  it("counts a readwrite grant as its read counterpart", () => {
    const visible = hrefs({
      grantedScopes: ["glucose.readwrite"],
      anonymous: false,
    });

    expect(visible).toContain("/reports/readings");
  });

  it("offers nothing when the viewer's scopes are unknown", () => {
    expect(
      visibleReportCategories({ grantedScopes: [], anonymous: true })
    ).toEqual([]);
  });

  it("drops a category whose every report is filtered out", () => {
    const ids = visibleReportCategories({
      grantedScopes: GLUCOSE_ONLY_SHARE,
      anonymous: true,
    }).map((c) => c.id);

    expect(ids).not.toContain("treatment");
    expect(ids).not.toContain("lifestyle");
  });
});

describe("getSidebarReportItems", () => {
  it("lists only available reports the viewer can load", () => {
    const items = getSidebarReportItems({
      grantedScopes: VIEWER,
      anonymous: false,
    });

    expect(items.map((i) => i.href)).not.toContain("/reports/steps");
    // "Hourly Patterns" is coming-soon and never belongs in the sidebar.
    expect(items.map((i) => i.href)).not.toContain("/reports/hourly-stats");
    expect(items.map((i) => i.title)).toContain("AGP");
  });
});
