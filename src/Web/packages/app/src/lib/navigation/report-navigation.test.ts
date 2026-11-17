import { describe, it, expect } from "vitest";
import {
  reportCategories,
  visibleReportCategories,
  getSidebarReportItems,
  reportDescription,
  reportSubRouteTitle,
  reportTitle,
  type ReportViewer,
} from "./report-navigation.svelte";

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

const everyHref = reportCategories()
  .flatMap((c) => c.reports)
  .map((r) => r.href);

describe("reportCategories", () => {
  it("lists each destination once", () => {
    expect(new Set(everyHref).size).toBe(everyHref.length);
  });

  it("offers no report that only forwards to another page", () => {
    expect(everyHref).not.toContain("/reports/month-to-month");
  });

  it("names the year overview for what it shows on every surface", () => {
    const yearOverview = reportCategories()
      .flatMap((c) => c.reports)
      .find((r) => r.href === "/reports/year-overview");

    expect(yearOverview?.title).toBe("Year overview");
    expect(
      getSidebarReportItems({ grantedScopes: OWNER, anonymous: false }).find(
        (i) => i.href === "/reports/year-overview"
      )?.title
    ).toBe("Year overview");
  });
});

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

    expect(visible).toEqual(["/reports/year-overview", "/reports/readings"]);
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
    expect(items.map((i) => i.href)).toContain("/reports/hourly-stats");
    expect(items.map((i) => i.title)).toContain("AGP");
  });

  it("leaves a coming-soon report out even when the viewer holds its scopes", () => {
    const items = getSidebarReportItems({
      grantedScopes: [...VIEWER, "treatments.read", "food.read"],
      anonymous: false,
    });

    expect(items.map((i) => i.href)).not.toContain("/reports/meals");
  });
});

describe("a report made of several pages", () => {
  const loop = () => reportCategories().find((c) => c.id === "loop")!;

  it("is one sidebar entry at the report's address", () => {
    const items = getSidebarReportItems({ grantedScopes: OWNER, anonymous: false });
    const entries = items.filter((i) => i.href.startsWith("/reports/loop-analysis"));
    expect(entries).toEqual([expect.objectContaining({ title: "Loop analysis", href: "/reports/loop-analysis" })]);
  });

  it("names each page with the report", () => {
    const category = loop();
    expect(category.reports.map((r) => reportTitle(category, r))).toContain("Loop analysis: Daily profile");
  });

  it("is found by searching for the report", () => {
    expect(loop().reports.find((r) => r.href.endsWith("/daily-profile"))?.keywords).toContain("loop analysis");
  });
});

describe("reportSubRouteTitle", () => {
  it.each([
    ["/reports/data-quality/compression-lows", "Compression lows"],
    ["/reports/data-quality/sensor-integrity", "Signal integrity"],
    ["/reports/data-quality/cgm-comparison", "CGM comparison"],
    ["/reports/data-quality/rejected-uploads", "Rejected uploads"],
    ["/reports/sleep/2026-10-01", "Sleep night report"],
  ])("names %s", (path, name) => {
    expect(reportSubRouteTitle(path)).toBe(name);
  });

  it("has no name for an unknown path", () => {
    expect(reportSubRouteTitle("/reports/unknown")).toBeUndefined();
    expect(reportSubRouteTitle("/reports/sleep")).toBeUndefined();
  });
});

const reportPages = import.meta.glob("/src/routes/*/reports/**/+page.svelte", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;

const reportPathnames = Object.keys(reportPages).map((file) =>
  file
    .replace("/src/routes/(authenticated)", "")
    .replace("/+page.svelte", "")
    .replace("[date]", "2026-10-01")
);

describe("reportDescription", () => {
  it("finds the report pages on disk", () => {
    expect(reportPathnames.length).toBeGreaterThan(20);
  });

  it.each(reportPathnames)("gives %s a description", (pathname) => {
    expect(reportDescription(pathname)?.trim()).toBeTruthy();
  });

  it("is the only place a report page sets a description", () => {
    for (const source of Object.values(reportPages)) {
      expect(source).not.toMatch(/name="description"/);
    }
  });

  it("has none for a path that is not a report", () => {
    expect(reportDescription("/reports/unknown")).toBeUndefined();
  });
});
