import { resolve } from "$app/paths";
import { a1cLabel } from "#lib/utils/a1c-formatting.js";
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type IconComponent = any;
import Activity from "@lucide/svelte/icons/activity";
import BarChart3 from "@lucide/svelte/icons/chart-column";
import BatteryFull from "@lucide/svelte/icons/battery-full";
import ArrowLeftRight from "@lucide/svelte/icons/arrow-left-right";
import Calendar from "@lucide/svelte/icons/calendar";
import ChartLine from "@lucide/svelte/icons/chart-line";
import CalendarDays from "@lucide/svelte/icons/calendar-days";
import Clock from "@lucide/svelte/icons/clock";
import Dumbbell from "@lucide/svelte/icons/dumbbell";
import FileText from "@lucide/svelte/icons/file-text";
import Footprints from "@lucide/svelte/icons/footprints";
import Gauge from "@lucide/svelte/icons/gauge";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import Layers from "@lucide/svelte/icons/layers";
import LayoutDashboard from "@lucide/svelte/icons/layout-dashboard";
import Moon from "@lucide/svelte/icons/moon";
import PieChart from "@lucide/svelte/icons/chart-pie";
import Repeat from "@lucide/svelte/icons/repeat";
import Sunrise from "@lucide/svelte/icons/sunrise";
import SunMoon from "@lucide/svelte/icons/sun-moon";
import Syringe from "@lucide/svelte/icons/syringe";
import Utensils from "@lucide/svelte/icons/utensils";
import SlidersHorizontal from "@lucide/svelte/icons/sliders-horizontal";
import Target from "@lucide/svelte/icons/target";
import { SiteChangeIcon } from "@nocturne/charts/icons";
import { satisfiesAllScopes } from "#lib/authorization/scopes.js";

export interface ReportItem {
  /** Title for the reports overview page */
  title: string;
  /** Shorter title for sidebar navigation (defaults to title) */
  sidebarTitle?: string;
  /** Description shown on reports overview page */
  description: string;
  href: string;
  /** Extra terms the command palette matches a search against. */
  keywords?: string[];
  icon: IconComponent;
  status: "available" | "coming-soon";
  /**
   * Every read scope the page's data calls need, taken from the
   * `[RequireScope]` on the endpoints they reach. A viewer holding less than
   * all of them cannot load the report, so it is not offered.
   */
  scopes: string[];
  /**
   * The report reads an endpoint that requires an authenticated caller whatever
   * its scopes (the therapy profile behind IDP), so it is hidden from anonymous
   * share viewers rather than rendered as a guaranteed 401.
   */
  memberOnly?: boolean;
}

export interface ReportCategory {
  id: "overview" | "patterns" | "lifestyle" | "treatment" | "loop";
  title: string;
  subtitle: string;
  icon: IconComponent;
  reports: ReportItem[];
  /**
   * The reports are the pages of one report, opened at this address, and share its filters. The
   * report header links between them (`ReportPageSwitcher`), the sidebar offers the report as one
   * entry, and each page is titled with the report's name ({@link reportTitle}).
   */
  pages?: { href: string };
}

/** Read scopes, spelled as the API's `OAuthScopes` spells them. */
const READ = {
  glucose: "glucose.read",
  treatments: "treatments.read",
  devices: "devices.read",
  therapy: "therapy.read",
  heartRate: "heartrate.read",
  stepCount: "stepcount.read",
  sleep: "sleep.read",
  food: "food.read",
  reports: "reports.read",
} as const;

/**
 * Scopes the reports overview itself needs: it renders the same range analytics
 * as the executive summary, so it must not be reached — or fetched for —
 * without them.
 */
export const reportsOverviewScopes: string[] = [READ.glucose, READ.reports];

export function estimatedA1cTitle(): string {
  return `Estimated ${a1cLabel()}`;
}

const ENGLISH_REPORT_KEYWORDS = {
  executiveSummary: ["overview", "summary", "report card", "snapshot"],
  agp: ["ambulatory glucose profile", "percentiles", "target range"],
  glucoseDistribution: ["spread"],
  dataQuality: ["compression", "signal", "sensor", "rejected uploads"],
  ehba1c: ["a1c", "hba1c", "glycated hemoglobin"],
  custom: ["custom", "saved", "builder", "report builder"],
  yearOverview: ["annual", "yearly", "12 month", "heatmap"],
  readings: ["glucose readings", "blood sugar", "log", "history"],
  dayInReview: ["daily", "today", "yesterday"],
  weekToWeek: ["comparison", "trend", "weekly"],
  comparison: ["compare", "date ranges", "diff"],
  hourlyStats: ["hourly", "time of day", "best hours", "worst hours"],
  steps: ["activity", "walking", "exercise", "movement"],
  heartRate: ["pulse", "bpm", "cardio"],
  sleep: ["overnight", "night time", "sleeping"],
  treatments: ["bolus", "carbs", "corrections", "insulin"],
  basalAnalysis: ["basal rate", "background insulin", "temp basal"],
  insulinDelivery: ["tdd", "total daily dose", "basal bolus ratio"],
  siteChangeImpact: ["infusion site", "cannula", "rotation", "set change"],
  idp: ["insulin dosing profile", "daily insulin", "dose adjustment"],
  battery: ["pump battery", "phone battery", "charge"],
  loopDailyProfile: ["loop analysis", "loopalyzer", "closed loop", "aid", "smb", "temp basal", "iob", "cob"],
  loopMealsAndEvents: ["loop analysis", "meal response", "corrections", "unannounced meals"],
  loopPredictionAccuracy: ["loop analysis", "predictions", "forecast", "uam", "zt"],
  loopHealth: ["loop analysis", "closed loop", "open loop", "limits", "sensitivity ratio"],
  loopSettingsEvidence: ["loop analysis", "basal", "isf", "carb ratio", "profile settings"],
} satisfies Record<string, string[]>;

export type ReportKeywordKey = keyof typeof ENGLISH_REPORT_KEYWORDS;

/**
 * The other words a search finds a report by, in the current locale, keyed by the report. A search also matches
 * the English words, which `ENGLISH_REPORT_KEYWORDS` keeps in step with these; "blood sugar" is only there, as
 * {@link searchTermWords} explains, and so is "diff", a short form with nothing to translate. Abbreviations are
 * written in capitals, as the interface writes them, so they share their translation with it; the search ignores case.
 */
export function reportKeywordWords(): Record<ReportKeywordKey, string[]> {
  return {
    // @wc-include
    executiveSummary: ["overview", "summary", "report card", "snapshot"],
    // @wc-include
    agp: ["ambulatory glucose profile", "percentiles", "target range"],
    // @wc-include
    glucoseDistribution: ["spread"],
    // @wc-include
    dataQuality: ["compression", "signal", "sensor", "rejected uploads"],
    // @wc-include
    ehba1c: ["a1c", "hba1c", "glycated hemoglobin"],
    // @wc-include
    custom: ["custom", "saved", "builder", "report builder"],
    // @wc-include
    yearOverview: ["annual", "yearly", "12 month", "heatmap"],
    // @wc-include
    readings: ["glucose readings", "log", "history"],
    // @wc-include
    dayInReview: ["daily", "today", "yesterday"],
    // @wc-include
    weekToWeek: ["comparison", "trend", "weekly"],
    // @wc-include
    comparison: ["compare", "date ranges"],
    // @wc-include
    hourlyStats: ["hourly", "time of day", "best hours", "worst hours"],
    // @wc-include
    steps: ["activity", "walking", "exercise", "movement"],
    // @wc-include
    heartRate: ["pulse", "BPM", "cardio"],
    // @wc-include
    sleep: ["overnight", "night time", "sleeping"],
    // @wc-include
    treatments: ["bolus", "carbs", "corrections", "insulin"],
    // @wc-include
    basalAnalysis: ["basal rate", "background insulin", "temp basal"],
    // @wc-include
    insulinDelivery: ["TDD", "total daily dose", "basal bolus ratio"],
    // @wc-include
    siteChangeImpact: ["infusion site", "cannula", "rotation", "set change"],
    // @wc-include
    idp: ["insulin dosing profile", "daily insulin", "dose adjustment"],
    // @wc-include
    battery: ["pump battery", "phone battery", "charge"],
    // @wc-include
    loopDailyProfile: ["loop analysis", "loopalyzer", "closed loop", "AID", "SMB", "temp basal", "IOB", "COB"],
    // @wc-include
    loopMealsAndEvents: ["loop analysis", "meal response", "corrections", "unannounced meals"],
    // @wc-include
    loopPredictionAccuracy: ["loop analysis", "predictions", "forecast", "UAM", "ZT"],
    // @wc-include
    loopHealth: ["loop analysis", "closed loop", "open loop", "limits", "sensitivity ratio"],
    // @wc-include
    loopSettingsEvidence: ["loop analysis", "basal", "ISF", "carb ratio", "profile settings"],
  };
}

/** Search words: the locale's, then the English ones it does not repeat. */
export function withEnglishWords(translated: readonly string[], english: readonly string[]): string[] {
  const found = [...translated, ...english];
  return found.filter((word, i) => found.indexOf(word) === i);
}

/** A report's search words. */
export function reportKeywords(key: ReportKeywordKey, translated: Partial<Record<ReportKeywordKey, string[]>>): string[] {
  return withEnglishWords(translated[key] ?? [], ENGLISH_REPORT_KEYWORDS[key]);
}

/**
 * Built per call so the titles are read in the current locale: wuchale
 * translates strings inside a function on each call, and a module-level
 * constant only once.
 */
export function reportCategories(words: Partial<Record<ReportKeywordKey, string[]>> = reportKeywordWords()): ReportCategory[] {
  return [
    {
      id: "overview",
      title: "The big picture",
      subtitle: "Your key metrics at a glance",
      icon: Gauge,
      reports: [
        {
          title: "Executive summary",
          description: "Time in range, average glucose and data completeness",
          href: "/reports/executive-summary",
          keywords: reportKeywords("executiveSummary", words),
          scopes: [READ.glucose, READ.reports],
          icon: Gauge,
          status: "available",
        },
        {
          title: "Glucose profile (AGP)",
          sidebarTitle: "AGP",
          description: "Your typical day of glucose, with percentile bands and time in range",
          href: "/reports/agp",
          keywords: reportKeywords("agp", words),
          scopes: [READ.glucose, READ.reports],
          icon: BarChart3,
          status: "available",
        },
        {
          title: "Glucose distribution",
          description: "Time spent in each glucose zone",
          href: "/reports/glucose-distribution",
          keywords: reportKeywords("glucoseDistribution", words),
          scopes: [READ.reports],
          icon: PieChart,
          status: "available",
        },
        {
          title: "Data quality",
          description: "Compression lows, signal integrity, CGM comparison and rejected uploads",
          href: "/reports/data-quality",
          keywords: reportKeywords("dataQuality", words),
          scopes: [READ.glucose, READ.reports],
          icon: Layers,
          status: "available",
        },
        {
          title: estimatedA1cTitle(),
          sidebarTitle: a1cLabel(true),
          description: `How your ${a1cLabel(true)} has trended over the years`,
          href: "/reports/ehba1c",
          keywords: reportKeywords("ehba1c", words),
          scopes: [READ.glucose, READ.reports],
          icon: Activity,
          status: "available",
        },
        {
          title: "Custom reports",
          description: "Reports that you make from the charts and figures of your data",
          href: "/reports/custom",
          keywords: reportKeywords("custom", words),
          scopes: [READ.reports],
          icon: LayoutDashboard,
          status: "available",
          memberOnly: true,
        },
      ],
    },
    {
      id: "patterns",
      title: "Patterns & trends",
      subtitle: "Discover what affects your glucose",
      icon: CalendarDays,
      reports: [
        {
          title: "Year overview",
          description: "Multi-year heatmap of your diabetes data",
          href: "/reports/year-overview",
          keywords: reportKeywords("yearOverview", words),
          scopes: [READ.glucose],
          icon: CalendarDays,
          status: "available",
        },
        {
          title: "Day-by-day view",
          sidebarTitle: "Readings",
          description: "Your glucose readings, one day at a time",
          href: "/reports/readings",
          keywords: reportKeywords("readings", words),
          scopes: [READ.glucose],
          icon: Calendar,
          status: "available",
        },
        {
          title: "Day in review",
          description: "Glucose and treatments for one day",
          href: "/reports/day-in-review",
          keywords: reportKeywords("dayInReview", words),
          scopes: [READ.glucose, READ.treatments, READ.reports],
          icon: Clock,
          status: "available",
        },
        {
          title: "Week to week",
          description: "Compare your glucose patterns from week to week",
          href: "/reports/week-to-week",
          keywords: reportKeywords("weekToWeek", words),
          scopes: [READ.glucose, READ.reports],
          icon: Sunrise,
          status: "available",
        },
        {
          title: "Comparison",
          description: "Compare two date ranges side by side",
          href: "/reports/comparison",
          keywords: reportKeywords("comparison", words),
          scopes: [READ.reports],
          icon: ArrowLeftRight,
          status: "available",
        },
        {
          title: "Hourly patterns",
          description: "Which hours of the day spend the most and least time in range",
          href: "/reports/hourly-stats",
          keywords: reportKeywords("hourlyStats", words),
          scopes: [READ.reports],
          icon: SunMoon,
          status: "available",
        },
      ],
    },
    {
      id: "lifestyle",
      title: "Lifestyle impact",
      subtitle: "How food, exercise & sleep affect you",
      icon: HeartPulse,
      reports: [
        {
          title: "Step count",
          sidebarTitle: "Steps",
          description: "Step count patterns with a glucose overlay",
          href: "/reports/steps",
          keywords: reportKeywords("steps", words),
          scopes: [READ.glucose, READ.stepCount],
          icon: Footprints,
          status: "available",
        },
        {
          title: "Heart rate",
          description: "Heart rate patterns with a glucose overlay",
          href: "/reports/heart-rate",
          keywords: reportKeywords("heartRate", words),
          scopes: [READ.glucose, READ.heartRate],
          icon: HeartPulse,
          status: "available",
        },
        {
          title: "Sleep & overnight",
          sidebarTitle: "Sleep",
          description: "Sleep patterns with a glucose overlay",
          href: "/reports/sleep",
          keywords: reportKeywords("sleep", words),
          scopes: [READ.glucose, READ.sleep],
          icon: Moon,
          status: "available",
        },
        {
          title: "Meal analysis",
          description: "See how different meals affect you",
          href: "/reports/meals",
          scopes: [READ.treatments, READ.food],
          icon: Utensils,
          status: "coming-soon",
        },
        {
          title: "Exercise impact",
          description: "Track activity's effect on glucose",
          href: "/reports/exercise",
          scopes: [READ.glucose, READ.stepCount],
          icon: Dumbbell,
          status: "coming-soon",
        },
      ],
    },
    {
      id: "treatment",
      title: "Treatment insights",
      subtitle: "Is your treatment working?",
      icon: Syringe,
      reports: [
        {
          title: "Treatment log",
          sidebarTitle: "Treatments",
          description: "View and edit your insulin doses and carb entries",
          href: "/reports/treatments",
          keywords: reportKeywords("treatments", words),
          scopes: [READ.glucose, READ.treatments, READ.devices, READ.reports],
          icon: FileText,
          status: "available",
        },
        {
          title: "Basal rate analysis",
          sidebarTitle: "Basal analysis",
          description: "Basal insulin delivery by time of day, with percentile bands",
          href: "/reports/basal-analysis",
          keywords: reportKeywords("basalAnalysis", words),
          scopes: [READ.reports],
          icon: Layers,
          status: "available",
        },
        {
          title: "Insulin delivery",
          description: "Basal and bolus split, and total daily dose trends",
          href: "/reports/insulin-delivery",
          keywords: reportKeywords("insulinDelivery", words),
          scopes: [READ.reports],
          icon: PieChart,
          status: "available",
        },
        {
          title: "Site change impact",
          description: "How pump site changes affect your glucose",
          href: "/reports/site-change-impact",
          keywords: reportKeywords("siteChangeImpact", words),
          scopes: [READ.devices, READ.reports],
          icon: SiteChangeIcon,
          status: "available",
        },
        {
          title: "Insulin dosing profile",
          sidebarTitle: "IDP",
          description: "Standardized insulin and glucose summary",
          href: "/reports/idp",
          keywords: reportKeywords("idp", words),
          scopes: [READ.treatments, READ.reports],
          icon: Syringe,
          status: "available",
          memberOnly: true,
        },
        {
          title: "Battery",
          description: "Device battery levels and charge cycles",
          href: "/reports/battery",
          keywords: reportKeywords("battery", words),
          scopes: [READ.devices],
          icon: BatteryFull,
          status: "available",
        },
      ],
    },
    {
      id: "loop",
      title: "Loop analysis",
      subtitle: "What your automated insulin delivery does",
      icon: Repeat,
      pages: { href: "/reports/loop-analysis" },
      reports: [
        {
          title: "Daily profile",
          description: "Glucose, basal, loop corrections, IOB and COB at each time of day",
          href: "/reports/loop-analysis/daily-profile",
          keywords: reportKeywords("loopDailyProfile", words),
          scopes: [READ.glucose, READ.treatments, READ.reports],
          icon: ChartLine,
          status: "available",
          memberOnly: true,
        },
        {
          title: "Meals and events",
          description: "Glucose and insulin before and after meals, corrections and other events",
          href: "/reports/loop-analysis/meals-and-events",
          keywords: reportKeywords("loopMealsAndEvents", words),
          scopes: [READ.glucose, READ.treatments, READ.reports],
          icon: Utensils,
          status: "available",
          memberOnly: true,
        },
        {
          title: "Prediction accuracy",
          description: "The loop's glucose predictions compared with the readings that followed",
          href: "/reports/loop-analysis/prediction-accuracy",
          keywords: reportKeywords("loopPredictionAccuracy", words),
          scopes: [READ.glucose, READ.treatments, READ.devices, READ.reports],
          icon: Target,
          status: "available",
          memberOnly: true,
        },
        {
          title: "Loop health",
          description: "How long the loop was closed, why it stopped and when it hit its limits",
          href: "/reports/loop-analysis/loop-health",
          keywords: reportKeywords("loopHealth", words),
          scopes: [READ.glucose, READ.treatments, READ.reports],
          icon: Repeat,
          status: "available",
          memberOnly: true,
        },
        {
          title: "Settings evidence",
          description: "Your profile settings compared with what your data shows",
          href: "/reports/loop-analysis/settings-evidence",
          keywords: reportKeywords("loopSettingsEvidence", words),
          scopes: [READ.glucose, READ.treatments, READ.therapy, READ.reports],
          icon: SlidersHorizontal,
          status: "available",
          memberOnly: true,
        },
      ],
    },
  ];
}

/**
 * The path of a report page under the app's base, with the query string it is shown with. `resolve`
 * takes only the route ids and pathnames SvelteKit generated, not a registry's runtime `href`.
 */
export function reportPagePath(href: string, search: { toString(): string } = "", hash = ""): string {
  const query = search.toString().replace(/^\?/, "");
  return `${resolve("").replace(/\/$/, "")}${href}${query ? `?${query}` : ""}${hash ? `#${hash}` : ""}`;
}

/** A report's full title: a page of a report made of several is named with that report. */
export function reportTitle(category: ReportCategory, report: ReportItem): string {
  return category.pages ? `${category.title}: ${report.title}` : report.title;
}

function reportSubRoutes(): Record<string, { title: string; description: string }> {
  return {
    "/reports/data-quality/compression-lows": {
      title: "Compression lows",
      description: "Glucose drops during sleep that can come from pressure on the sensor",
    },
    "/reports/data-quality/sensor-integrity": {
      title: "Signal integrity",
      description: "Days and windows where the sensor signal was unreliable",
    },
    "/reports/data-quality/cgm-comparison": {
      title: "CGM comparison",
      description: "Two CGM devices compared against each other",
    },
    "/reports/data-quality/rejected-uploads": {
      title: "Rejected uploads",
      description: "Records an uploader sent that Nocturne could not store",
    },
    "/reports/sleep/[date]": {
      title: "Sleep night report",
      description: "Sleep stages and overnight glucose for one night",
    },
    "/reports/custom/new": {
      title: "New custom report",
      description: "Make a report from the charts and figures of your data",
    },
    "/reports/custom/[id]/edit": {
      title: "Edit custom report",
      description: "Change the blocks of a saved custom report",
    },
    "/reports/custom/[id]": {
      title: "Custom report",
      description: "A saved custom report, run on your current data",
    },
  };
}

function subRouteKey(pathname: string): string {
  if (/^\/reports\/sleep\/[^/]+$/.test(pathname)) return "/reports/sleep/[date]";
  if (/^\/reports\/custom\/[^/]+\/edit$/.test(pathname)) return "/reports/custom/[id]/edit";
  if (/^\/reports\/custom\/[^/]+$/.test(pathname) && pathname !== "/reports/custom/new") return "/reports/custom/[id]";
  return pathname;
}

/**
 * The name of a report route the registry does not list, resolvable on the
 * server where a page's own effects do not run.
 */
export function reportSubRouteTitle(pathname: string): string | undefined {
  return reportSubRoutes()[subRouteKey(pathname)]?.title;
}

/**
 * The one meta description of a report page: the registry entry's description, the sub-route's, or
 * the reports index's. A path with none has no description.
 */
export function reportDescription(pathname: string): string | undefined {
  if (pathname === "/reports") return "Your glucose summary for the chosen period and every report you can open";
  const listed = reportCategories()
    .flatMap((c) => c.reports)
    .find((r) => r.href === pathname);
  return listed?.description ?? reportSubRoutes()[subRouteKey(pathname)]?.description;
}

/** The viewer a report list is built for. */
export interface ReportViewer {
  /**
   * The viewer's granted scopes, as `page.data.effectivePermissions` carries
   * them.
   */
  grantedScopes: readonly string[];
  /** Whether the viewer is a public share link rather than a signed-in member. */
  anonymous: boolean;
}

/**
 * Report categories the viewer can actually load: every report whose scopes the
 * viewer holds, minus the member-only ones for an anonymous share. Categories
 * left empty are dropped. A viewer whose scopes are unknown holds none, so
 * nothing is offered.
 */
export function visibleReportCategories(
  viewer: ReportViewer
): ReportCategory[] {
  return reportCategories()
    .map((c) => ({
      ...c,
      reports: c.reports.filter(
        (r) =>
          !(viewer.anonymous && r.memberOnly) &&
          satisfiesAllScopes(viewer.grantedScopes, r.scopes)
      ),
    }))
    .filter((c) => c.reports.length > 0);
}

/**
 * Flat list of available report items for sidebar navigation, per viewer. A report made of several
 * pages is one entry, at the report's own address.
 */
export function getSidebarReportItems(viewer: ReportViewer): {
  title: string;
  href: string;
  icon: IconComponent;
}[] {
  return visibleReportCategories(viewer).flatMap((c) => {
    const available = c.reports.filter((r) => r.status === "available");
    if (c.pages)
      return available.length > 0 ? [{ title: c.title, href: c.pages.href, icon: c.icon }] : [];
    return available.map((r) => ({
      title: r.sidebarTitle ?? r.title,
      href: r.href,
      icon: r.icon,
    }));
  });
}
