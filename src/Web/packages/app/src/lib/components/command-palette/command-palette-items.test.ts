import { afterEach, describe, expect, it } from "vitest";
import { glucoseChartLookback, glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import {
  paletteItems,
  paletteItemsFor,
  paletteKeywordWords,
  paletteStatValues,
  type PaletteKeywordKey,
} from "./command-palette-items.svelte.js";
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
    const offered = paletteItemsFor(false, OWNER).map((item) => item.id);

    expect(offered).toEqual(expect.arrayContaining(paletteItems().map((item) => item.id)));
  });

  it("reads the keywords from the translatable table when no words are passed", () => {
    const sage = paletteItemsFor(false, OWNER).find((item) => item.id === "stat-sage");

    expect(sage?.keywords).toContain("CGM age");
  });

  it("offers only the subject-scoped destinations on a tenantless host", () => {
    // Everything else bounces back to "/" via the route guard, so Cmd-K must not advertise it.
    expect(paletteItemsFor(true, OWNER).map((item) => item.id)).toEqual([
      "page-dashboard",
      "settings-account",
      "settings-teams",
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

describe("the demo visitor", () => {
  it("is not offered Teams by the palette flag", () => {
    const all = paletteItems();
    const teams = all.find((item) => item.id === "settings-teams");

    expect(teams?.notForDemoSubject).toBe(true);
    expect(all.filter((item) => item.notForDemoSubject).map((item) => item.id)).toEqual(["settings-teams"]);
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

describe("search keywords", () => {
  const german: Partial<Record<PaletteKeywordKey, string[]>> = {
    settingsAppearance: ["Erscheinungsbild", "dunkler Modus"],
    pageDashboard: ["Startseite"],
    actionManualSync: ["synchronisieren"],
  };

  const keywordsOf = (id: string, words = german) =>
    paletteItemsFor(false, OWNER, words).find((item) => item.id === id)?.keywords;

  it("finds a setting, a page and an action by a translated word and by the English one", () => {
    expect(keywordsOf("settings-appearance")).toEqual(expect.arrayContaining(["Erscheinungsbild", "dunkler Modus", "theme"]));
    expect(keywordsOf("page-dashboard")).toEqual(expect.arrayContaining(["Startseite", "overview"]));
    expect(keywordsOf("action-manual-sync")).toEqual(expect.arrayContaining(["synchronisieren", "refresh"]));
  });

  it("keeps one entry's translated words out of another's", () => {
    expect(keywordsOf("settings-account")).not.toContain("Erscheinungsbild");
    expect(keywordsOf("page-dashboard")).not.toContain("synchronisieren");
  });

  it("falls back to the English words for an entry with no translation", () => {
    expect(keywordsOf("settings-account", {})).toEqual(["account", "email", "password"]);
  });

  it("lists each word once", () => {
    const words = { settingsAccount: ["account", "Konto"] };

    expect(keywordsOf("settings-account", words)).toEqual(["account", "Konto", "email", "password"]);
  });

  it("has translatable words for every entry, and keeps an English word for each", () => {
    const translated = paletteKeywordWords();
    const keyOf = (id: string) => id.replace(/-(.)/g, (_, c: string) => c.toUpperCase()) as PaletteKeywordKey;
    const english = paletteItems({});
    const localised = paletteItems(translated);

    for (const item of paletteItems()) {
      const words = translated[keyOf(item.id)];
      expect(words?.length, item.id).toBeGreaterThan(0);
      expect(english.find((i) => i.id === item.id)?.keywords, item.id).toEqual(
        expect.arrayContaining(words.map((word) => word.toLowerCase())),
      );
      expect(localised.find((i) => i.id === item.id)?.keywords, item.id).toEqual(expect.arrayContaining(words));
    }
  });

  it("gives every entry that is not a report words to match", () => {
    for (const item of paletteItems()) {
      expect(item.keywords?.length, item.id).toBeGreaterThan(0);
    }
  });
});

describe("quick-settings labels", () => {
  const labelOf = (id: string) => paletteItems().find((item) => item.id === id)?.label;

  afterEach(() => {
    glucoseUnits.current = "mg/dl";
    glucoseChartLookback.current = 12;
  });

  it("shows the unit in use", () => {
    expect(labelOf("qs-glucose-units")).toBe("Units: mg/dL");

    glucoseUnits.current = "mmol";
    expect(labelOf("qs-glucose-units")).toBe("Units: mmol/L");
  });

  it("shows the chart lookback in use", () => {
    glucoseChartLookback.current = 6;
    expect(labelOf("qs-chart-lookback")).toBe("Chart lookback: 6h");
  });

  it("still finds the units and lookback items by the verbs they once carried", () => {
    const keywords = (id: string) => paletteItems().find((item) => item.id === id)?.keywords;

    expect(keywords("qs-glucose-units")).toContain("switch");
    expect(keywords("qs-chart-lookback")).toContain("change");
  });

  it("shows a dark mode that follows the system", () => {
    expect(labelOf("qs-dark-mode")).toBe("Dark mode: System");
  });
});

describe("stat labels", () => {
  const NOW = Date.UTC(2026, 9, 25, 12, 0, 0);
  const labelOf = (id: string, values = {}) =>
    paletteItems(undefined, values).find((item) => item.id === id)?.label;

  it("carry the live value after the name", () => {
    expect(labelOf("stat-iob", { iob: "1.2 U" })).toBe("IOB: 1.2 U");
    expect(labelOf("stat-cob", { cob: "5 g" })).toBe("COB: 5 g");
    expect(labelOf("stat-cage", { cage: "2d 2h" })).toBe("CAGE: 2d 2h");
    expect(labelOf("stat-sage", { sage: "5h" })).toBe("SAGE: 5h");
  });

  it("are the bare name while there is no value", () => {
    expect(labelOf("stat-iob")).toBe("IOB");
    expect(labelOf("stat-sage", { iob: "1.2 U" })).toBe("SAGE");
  });

  it("read the pills and the consumable ages off the realtime store", () => {
    const values = paletteStatValues({
      pillsData: { iob: { display: "1.2 U" } },
      nowMinute: NOW,
      legacyFallbacks: [
        { kind: "Site", latestEventAt: new Date(NOW - 50 * 3600_000).toISOString(), ageHours: 1 },
      ],
      legacyCoverage: [],
      trackerInstances: [],
    } as unknown as Parameters<typeof paletteStatValues>[0]);

    expect(values).toEqual({ iob: "1.2 U", cob: undefined, cage: "2d 2h", sage: undefined });
  });
});
