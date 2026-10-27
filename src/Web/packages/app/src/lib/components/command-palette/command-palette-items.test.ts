import { describe, expect, it } from "vitest";
import { items, paletteItemsFor } from "./command-palette-items";
import { isTenantlessRoute } from "#lib/navigation/tenantless-navigation.js";
import {
  reportCategories,
  reportTitle,
  visibleReportCategories,
  type ReportViewer,
} from "#lib/navigation/report-navigation.svelte.js";

const OWNER: ReportViewer = { grantedScopes: ["*"], anonymous: false };
const GLUCOSE_ONLY: ReportViewer = {
  grantedScopes: ["glucose.read"],
  anonymous: false,
};

function paletteReports(viewer: ReportViewer) {
  return paletteItemsFor(false, viewer).filter(
    (item) => item.group === "reports"
  );
}

function registryReports(viewer: ReportViewer) {
  return visibleReportCategories(viewer)
    .flatMap((c) => c.reports.map((r) => ({ ...r, title: reportTitle(c, r) })))
    .filter((r) => r.status === "available");
}

describe("paletteItemsFor", () => {
  it("offers every non-report entry on a host that resolves a tenant", () => {
    expect(paletteItemsFor(false, OWNER)).toEqual(
      expect.arrayContaining(items)
    );
  });

  it("offers only the subject-scoped destinations on a tenantless host", () => {
    // Everything else bounces back to "/" via the route guard, so Cmd-K must not advertise it.
    expect(paletteItemsFor(true, OWNER).map((item) => item.id)).toEqual([
      "page-dashboard",
      "settings-account",
      "settings-appearance",
    ]);
  });

  it("keeps no entry whose destination needs a resolved tenant", () => {
    for (const item of paletteItemsFor(true, OWNER)) {
      const destination = item.href ?? item.linkedHref;

      expect(destination, `${item.id} has no destination to reach`).toBeDefined();
      expect(isTenantlessRoute(destination!), `${item.id} leads to ${destination}`).toBe(true);
    }
  });

  it("drops the tenant-scoped groups the full list is mostly made of", () => {
    // Non-vacuity: the assertions above would pass on an empty or trivially small source list.
    const groups = new Set(paletteItemsFor(false, OWNER).map((item) => item.group));
    expect(groups).toContain("reports");
    expect(groups).toContain("settings");
    expect(groups).toContain("actions");

    const tenantlessGroups = new Set(paletteItemsFor(true, OWNER).map((item) => item.group));
    expect(tenantlessGroups).toEqual(new Set(["pages", "settings"]));
  });
});

describe("the reports group", () => {
  it("lists the registry's available reports for a full-scope viewer", () => {
    const expected = registryReports(OWNER);

    expect(expected.length).toBeGreaterThan(0);
    expect(
      paletteReports(OWNER).map((i) => [i.label, i.description, i.href, i.icon])
    ).toEqual(expected.map((r) => [r.title, r.description, r.href, r.icon]));
  });

  it("lists the registry's reports for a viewer short of some scopes", () => {
    const expected = registryReports(GLUCOSE_ONLY).map((r) => r.href);

    expect(expected.length).toBeLessThan(registryReports(OWNER).length);
    expect(paletteReports(GLUCOSE_ONLY).map((i) => i.href)).toEqual(expected);
  });

  it("withholds a report whose scope the viewer lacks", () => {
    const hrefs = paletteReports(GLUCOSE_ONLY).map((i) => i.href);

    expect(hrefs).toContain("/reports/year-overview");
    expect(hrefs).not.toContain("/reports/treatments");
    expect(hrefs).not.toContain("/reports/battery");
  });

  it("withholds a member-only report from an anonymous viewer", () => {
    const hrefs = paletteReports({
      grantedScopes: ["*"],
      anonymous: true,
    }).map((i) => i.href);

    expect(hrefs).not.toContain("/reports/idp");
    expect(hrefs).toContain("/reports/agp");
  });

  it("offers no report that is not yet available", () => {
    const comingSoon = reportCategories()
      .flatMap((c) => c.reports)
      .filter((r) => r.status === "coming-soon")
      .map((r) => r.href);

    expect(comingSoon.length).toBeGreaterThan(0);
    const hrefs = paletteReports(OWNER).map((i) => i.href);
    for (const href of comingSoon) expect(hrefs).not.toContain(href);
  });

  it("gives every report a distinct id", () => {
    const ids = paletteItemsFor(false, OWNER).map((i) => i.id);

    expect(new Set(ids).size).toBe(ids.length);
  });

  it("names each page of a report made of several with the report", () => {
    const labels = paletteReports(OWNER).map((i) => i.label);
    expect(labels).toContain("Loop analysis: Daily profile");
    expect(labels).toContain("Loop analysis: Settings evidence");
  });
});
