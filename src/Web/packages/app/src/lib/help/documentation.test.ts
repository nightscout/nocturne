import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  articles,
  articleFor,
  documentationLinks,
  searchDocumentation,
} from "./documentation.svelte";
import {
  buildAppNavigation,
  type NavItem,
} from "$lib/navigation/app-navigation.svelte";
import { glucoseBands } from "./reference.svelte";

const navigation: NavItem[] = [
  {
    id: "reports",
    title: "Reports",
    icon: null,
    children: [
      { title: "Steps", href: "/reports/steps", icon: null },
      { title: "Heart Rate", href: "/reports/heart-rate", icon: null },
    ],
  },
  {
    id: "settings",
    title: "Settings",
    icon: null,
    children: [
      { title: "Connectors & Apps", href: "/settings/connectors", icon: null },
    ],
  },
];
const viewer = {
  user: {},
  isGuestSession: false,
  isPlatformAdmin: true,
  grantedScopes: ["*"],
  tenantCount: 2,
  tenantless: false,
};

describe("contextual documentation", () => {
  const links = documentationLinks(navigation);
  it("provides a complete guide for every visible main navigation destination", () => {
    const all = articles();
    const nav = buildAppNavigation(viewer).flatMap(
      (item) => item.children ?? (item.href ? [{ href: item.href }] : [])
    );
    for (const link of nav) {
      const article = all.find((item) => item.href === link.href);
      expect(article, link.href).toBeDefined();
      expect(article!.sections.length).toBeGreaterThanOrEqual(4);
      expect(
        article!.sections.every((section) => section.text.length > 40)
      ).toBe(true);
    }
    expect(new Set(all.map((article) => article.href)).size).toBe(all.length);
  });
  it("resolves trailing slashes and dynamic detail pages to the relevant guide", () => {
    expect(articleFor("/reports/steps/", links).title).toBe("Steps");
    expect(articleFor("/settings/connectors/dexcom", links).title).toBe(
      "Dexcom"
    );
    expect(articleFor("/reports/sleep/2026-10-12", links).title).toBe(
      "Sleep night"
    );
    expect(articleFor("/alerts/editor/123", links).title).toBe("Alert Rules");
    expect(articleFor("/", links).title).toBe("Dashboard");
  });
  it("searches tutorial steps as well as article bodies", () => {
    expect(
      searchDocumentation("REDIRECT mismatch", links).map(
        (result) => result.title
      )
    ).toContain("Google Health");
    expect(
      searchDocumentation("test account", links).map((result) => result.title)
    ).toContain("Google Health");
    expect(searchDocumentation("   ", links)).toEqual([]);
    expect(searchDocumentation("unfindable", links)).toEqual([]);
  });
  it("offers connector guides only when navigation offers their parent, without mutating it", () => {
    expect(
      links.some((link) => link.href === "/settings/connectors/google-health")
    ).toBe(true);
    expect(
      documentationLinks(navigation.slice(0, 1)).some((link) =>
        link.href.startsWith("/settings/connectors")
      )
    ).toBe(false);
    expect(navigation[1].children).toHaveLength(1);
  });
  it("does not restore scope-filtered reports through the overview, including public shares", () => {
    for (const user of [{}, null]) {
      const visible = documentationLinks(
        buildAppNavigation({
          ...viewer,
          user,
          isPlatformAdmin: false,
          grantedScopes: ["glucose.read", "reports.read"],
        })
      );
      expect(visible.some((link) => link.href === "/reports")).toBe(true);
      for (const href of [
        "/reports/steps",
        "/reports/heart-rate",
        "/reports/sleep",
        "/reports/idp",
      ]) {
        expect(
          visible.some((link) => link.href === href),
          href
        ).toBe(false);
      }
      expect(
        searchDocumentation("steps", visible).some(
          (article) => article.href === "/reports/steps"
        )
      ).toBe(false);
      expect(
        visible.some(
          (link) => link.href === "/reports/data-quality/sensor-integrity"
        )
      ).toBe(true);
    }
  });
  it("indexes the shared glucose glossary labels", () => {
    const visible = documentationLinks(buildAppNavigation(viewer));
    expect(
      searchDocumentation("very high", visible).some(
        (article) => article.href === "/reports/agp"
      )
    ).toBe(true);
  });
  it("includes technical Google troubleshooting in the source translation catalogue", () => {
    const google = articleFor("/settings/connectors/google-health", links);
    const text = google.sections.find(
      (section) => section.title === "Common setup errors"
    )!.text;
    const catalogue = readFileSync(
      new URL("../../../../../locales/en.po", import.meta.url),
      "utf8"
    );
    expect(catalogue).toContain(`msgid ${JSON.stringify(text)}`);
  });
  it("keeps admin-only guides out of member and guest search", () => {
    const member = documentationLinks(
      buildAppNavigation({ ...viewer, isPlatformAdmin: false })
    );
    expect(
      member.some((link) => link.href === "/settings/admin/connector-cursors")
    ).toBe(false);
    const admin = documentationLinks(buildAppNavigation(viewer));
    expect(
      admin.some((link) => link.href === "/settings/admin/connector-cursors")
    ).toBe(true);
    const guest = documentationLinks(
      buildAppNavigation({ ...viewer, isGuestSession: true })
    );
    expect(guest.some((link) => link.href.startsWith("/settings/"))).toBe(
      false
    );
    expect(admin.some((link) => link.href.includes("[date]"))).toBe(false);
  });
  it("uses supplied thresholds instead of copying default glucose limits into help", () => {
    const bands = glucoseBands({
      veryLow: 55,
      low: 75,
      targetBottom: 75,
      targetTop: 190,
      tightTargetBottom: 75,
      tightTargetTop: 145,
      veryHigh: 260,
    });
    expect(bands.find((band) => band.key === "high")).toMatchObject({
      lower: 190,
      upper: 260,
      lowerInclusive: false,
      upperInclusive: true,
    });
    expect(bands.find((band) => band.key === "tight-range")).toMatchObject({
      lower: 75,
      upper: 145,
      lowerInclusive: true,
      upperInclusive: true,
    });
  });
  it("orders first setup before consent, inventory selection, and verification", () => {
    const google = articleFor("/settings/connectors/google-health", links);
    expect(google.steps).toHaveLength(13);
    expect(google.steps![0].text).toContain("visible in that app");
    expect(google.steps![0].links![0].href).toContain(
      "com.fitbit.FitbitMobile"
    );
    expect(google.steps![1].text).toContain("Garmin Connect");
    expect(google.steps![2].text).toContain(
      "Enable the API and get an OAuth 2.0 Client ID"
    );
    expect(google.steps![5].text).toContain("Web Server");
    expect(google.steps![6].text).toContain("required on first setup");
    expect(google.steps![7].text).toContain(
      "email address used in the Google Health app"
    );
    expect(google.steps![10].text).toContain("Save selection and import");
    expect(google.steps![11].text).toContain("choose dates");
    expect(JSON.stringify(google)).not.toContain("smokkelaar.duckdns.org");
    expect(
      google.sources!.every((source) => source.href.startsWith("https://"))
    ).toBe(true);
  });
});
