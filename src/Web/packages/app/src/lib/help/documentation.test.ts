import { describe, expect, it } from "vitest";
import {
  articleFor,
  documentationLinks,
  searchDocumentation,
} from "./documentation";
import type { NavItem } from "$lib/navigation/app-navigation.svelte";

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

describe("contextual documentation", () => {
  const links = documentationLinks(navigation);
  it("resolves trailing slashes and provides help for unknown nested pages", () => {
    expect(articleFor("/reports/steps/", links).title).toBe("Steps");
    expect(
      articleFor("/settings/connectors/dexcom", links).introduction
    ).toContain("dexcom");
    expect(articleFor("/", links).title).toBe("Dashboard");
  });
  it("searches article bodies with multiple terms, case-insensitively", () => {
    expect(
      searchDocumentation("HEART types", links).map((result) => result.title)
    ).toContain("Google Health");
    expect(searchDocumentation("   ", links)).toEqual([]);
    expect(searchDocumentation("unfindable", links)).toEqual([]);
  });
  it("offers connector guides only when the app navigation offers connectors", () => {
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
});
