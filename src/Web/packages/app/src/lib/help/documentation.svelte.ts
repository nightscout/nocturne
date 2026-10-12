import type { NavItem, NavLink } from "$lib/navigation/app-navigation.svelte";
import type { HelpArticle } from "./documentation-types";
import { pageArticles } from "./pages.svelte";
import { reportArticles } from "./reports.svelte";
import { settingsArticles } from "./settings.svelte";
import { connectorArticles } from "./connectors.svelte";
import { glucoseBands } from "./reference.svelte";

// Per-call factories let translated titles follow the current locale.
export function articles(): HelpArticle[] {
  return [
    ...pageArticles(),
    ...reportArticles(),
    ...settingsArticles(),
    ...connectorArticles(),
  ];
}

function parentFor(href: string): string | undefined {
  // The report overview is not permission to visit every report.
  if (href.startsWith("/reports/"))
    return href.startsWith("/reports/data-quality/")
      ? "/reports/data-quality"
      : href;
  if (href === "/settings/weight" || href === "/settings/timezone")
    return "/settings/patient";
  if (
    href === "/settings/migration" ||
    href === "/settings/nightscout-transition"
  )
    return "/settings/connectors";
  if (href === "/settings/integrations/discord") return "/settings/account";
  if (
    href.startsWith("/settings/admin/") ||
    href === "/settings/access-requests" ||
    href === "/settings/audit"
  )
    return "/settings/admin/tenants";
  return undefined;
}

export function documentationLinks(navigation: NavItem[]): NavLink[] {
  const links = navigation.flatMap((item) =>
    item.children
      ? [...item.children]
      : item.href
        ? [{ ...item, href: item.href }]
        : []
  );
  const allowed = links.map((link) => link.href);
  for (const article of articles()) {
    if (article.href.includes("[") || allowed.includes(article.href)) continue;
    const explicit = parentFor(article.href);
    const parent = explicit
      ? links.find((link) => link.href === explicit)
      : links.find(
          (link) =>
            allowed.includes(link.href) &&
            link.href !== "/" &&
            article.href.startsWith(`${link.href}/`)
        );
    if (parent)
      links.push({
        ...parent,
        strict: true,
        title: article.title,
        href: article.href,
      });
  }
  return links;
}

export function articleFor(path: string, links: NavLink[]): HelpArticle {
  const normalized = path.replace(/\/+$/, "") || "/";
  const all = articles();
  const exact = all.find((entry) => entry.href === normalized);
  if (exact) return exact;
  if (/^\/reports\/sleep\/[^/]+$/.test(normalized))
    return all.find((entry) => entry.href === "/reports/sleep/[date]")!;
  const parent = all
    .filter(
      (entry) => entry.href !== "/" && normalized.startsWith(`${entry.href}/`)
    )
    .sort((a, b) => b.href.length - a.href.length)[0];
  if (parent) return { ...parent, href: normalized };
  const title =
    links.find((link) => link.href === normalized)?.title ?? "Page guide";
  return {
    href: normalized,
    title,
    introduction:
      "Use this page's visible controls and field descriptions to inspect its information.",
    sections: [
      {
        title: "Settings and access",
        text: "Available actions depend on the selected tenant and your permissions. Check the related settings in the navigation menu. Close documentation with the book or Escape to return to your original page.",
      },
    ],
    related: [],
  };
}

export function searchDocumentation(query: string, links: NavLink[]) {
  const terms = query.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);
  if (!terms.length) return [];
  return links
    .map((link) => articleFor(link.href, links))
    .filter((article) => {
      const text = [
        article.title,
        article.introduction,
        ...(article.reference === "glucose"
          ? glucoseBands({}).map((band) => band.title)
          : []),
        ...article.sections.flatMap((section) => [section.title, section.text]),
        ...(article.steps ?? []).flatMap((step) => [step.title, step.text]),
      ]
        .join(" ")
        .toLocaleLowerCase();
      return terms.every((term) => text.includes(term));
    });
}
