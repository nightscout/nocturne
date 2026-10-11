import type { NavItem, NavLink } from "$lib/navigation/app-navigation.svelte";

// Content is keyed by app paths; adding an article never creates a second app route.
export function articles() {
  return [
    {
      href: "/reports/steps",
      title: "Steps",
      introduction:
        "This guide covers Steps: counting steps and viewing your activity over time.",
      sections: [
        {
          title: "What am I looking at?",
          text: "This report shows step counts received by Nocturne. Select a date range to explore your activity alongside your glucose readings.",
        },
        {
          title: "Getting started",
          text: "You need a source that records steps and shares them with Nocturne. Google Health supports step imports. Check that your source contains step data before connecting it.",
        },
        {
          title: "Missing data?",
          text: "Check the selected dates, your connector status, and whether step syncing is enabled. An empty report does not necessarily mean you took no steps.",
        },
      ],
      related: ["/settings/connectors/google-health", "/settings/connectors"],
    },
    {
      href: "/reports/heart-rate",
      title: "Heart Rate",
      introduction:
        "This guide covers Heart Rate: your recorded heart rate and patterns over time.",
      sections: [
        {
          title: "Getting started",
          text: "You need a device or service that records heart rate, plus a supported connection to Nocturne. Google Health can import heart rate when those readings are available in your account.",
        },
        {
          title: "Choose a connection",
          text: "First check where your device stores its readings. Then open the connector guide below to find out how to connect that account and enable heart rate syncing.",
        },
        {
          title: "Adjust the view",
          text: "Use the report's date controls to choose the period you want to review. If readings are missing, check the source account, connector status, and selected data types.",
        },
      ],
      related: ["/settings/connectors/google-health", "/settings/connectors"],
    },
    {
      href: "/settings/connectors",
      title: "Connectors & Apps",
      introduction:
        "This guide covers Connectors & Apps. Connectors bring data from other services into Nocturne.",
      sections: [
        {
          title: "Choose your source",
          text: "Choose the service where your device stores its data. Each connector supports different data types. Google Health supports steps, heart rate, weight, and sleep.",
        },
        {
          title: "Check your connection",
          text: "Review the connection status and the latest sync. Open the connector's settings to manage its account and data types. Available controls depend on your permissions.",
        },
      ],
      related: [
        "/settings/connectors/google-health",
        "/reports/steps",
        "/reports/heart-rate",
      ],
    },
    {
      href: "/settings/connectors/google-health",
      title: "Google Health",
      introduction:
        "Google Health can bring steps, heart rate, weight, and sleep into Nocturne. You need a Google Health account containing the data you want to import.",
      sections: [
        {
          title: "Connect your account",
          text: "Open the Google Health connector page, follow the account connection flow, and review the requested permissions. Some setup options require an administrator.",
        },
        {
          title: "Choose what to sync",
          text: "Select the available data types you want to share. For the Steps report enable steps; for Heart Rate enable heart rate. Check your connection status after saving.",
        },
        {
          title: "Troubleshooting",
          text: "If your report is empty, confirm that the source account contains readings for the selected dates and that the relevant data type is enabled. Review the connector's status for connection or sync errors.",
        },
      ],
      related: [
        "/reports/steps",
        "/reports/heart-rate",
        "/settings/connectors",
      ],
    },
    {
      href: "/settings/appearance",
      title: "Appearance",
      introduction:
        "This guide covers Appearance: customizing how Nocturne looks and how help is displayed.",
      sections: [
        {
          title: "Help visibility",
          text: "Show the full help bar, keep only the library icon, or hide help completely. You can always enable it again here. This preference is saved in this browser.",
        },
      ],
      related: [],
    },
  ];
}

export function documentationLinks(navigation: NavItem[]): NavLink[] {
  const links = navigation.flatMap(
    (item) => item.children ?? (item.href ? [{ ...item, href: item.href }] : [])
  );
  for (const article of articles()) {
    if (links.some((link) => link.href === article.href)) continue;
    const parent = links.find(
      (link) => link.href !== "/" && article.href.startsWith(`${link.href}/`)
    );
    if (parent)
      links.push({ ...parent, title: article.title, href: article.href });
  }
  return links;
}

export function articleFor(path: string, links: NavLink[]) {
  const normalized = path.replace(/\/+$/, "") || "/";
  const article = articles().find((entry) => entry.href === normalized);
  if (article) return article;
  const link = links.find((entry) => entry.href === normalized);
  const title =
    link?.title ??
    normalized.split("/").filter(Boolean).at(-1)?.replaceAll("-", " ") ??
    "Dashboard";
  return {
    href: normalized,
    title,
    introduction: `This guide covers ${title}.`,
    sections: [
      {
        title: "About this page",
        text: "This is a starting point for this page's documentation. More explanations, settings, and instructions can be added here.",
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
        ...article.sections.flatMap((section) => [section.title, section.text]),
      ]
        .join(" ")
        .toLocaleLowerCase();
      return terms.every((term) => text.includes(term));
    });
}
