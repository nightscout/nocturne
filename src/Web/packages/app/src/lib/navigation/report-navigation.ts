// eslint-disable-next-line @typescript-eslint/no-explicit-any
type IconComponent = any;
import {
  BarChart3,
  BatteryFull,
  ArrowLeftRight,
  Calendar,
  CalendarDays,
  Clock,
  Dumbbell,
  FileText,
  Footprints,
  Gauge,
  HeartPulse,
  Layers,
  Moon,
  PieChart,
  Sunrise,
  Syringe,
  Utensils,
} from "lucide-svelte";
import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
import { satisfiesAllScopes } from "$lib/authorization/scopes";

export interface ReportItem {
  /** Title for the reports overview page */
  title: string;
  /** Shorter title for sidebar navigation (defaults to title) */
  sidebarTitle?: string;
  /** Description shown on reports overview page */
  description: string;
  href: string;
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
  id: "overview" | "patterns" | "lifestyle" | "treatment";
  title: string;
  subtitle: string;
  icon: IconComponent;
  reports: ReportItem[];
}

/** Read scopes, spelled as the API's `OAuthScopes` spells them. */
const READ = {
  glucose: "glucose.read",
  treatments: "treatments.read",
  devices: "devices.read",
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

export const reportCategories: ReportCategory[] = [
  {
    id: "overview",
    title: "The Big Picture",
    subtitle: "Your key metrics at a glance",
    icon: Gauge,
    reports: [
      {
        title: "Executive Summary",
        description: "All your important numbers in one place",
        href: "/reports/executive-summary",
        scopes: [READ.glucose, READ.reports],
        icon: Gauge,
        status: "available",
      },
      {
        title: "Glucose Profile (AGP)",
        sidebarTitle: "AGP",
        description: "Your typical day's glucose pattern",
        href: "/reports/agp",
        scopes: [READ.glucose, READ.reports],
        icon: BarChart3,
        status: "available",
      },
      {
        title: "Glucose Distribution",
        description: "Time spent in each glucose zone",
        href: "/reports/glucose-distribution",
        scopes: [READ.reports],
        icon: PieChart,
        status: "available",
      },
      {
        title: "Data Quality",
        description: "Assess the reliability of your data",
        href: "/reports/data-quality",
        scopes: [READ.glucose, READ.reports],
        icon: Layers,
        status: "available",
      },
    ],
  },
  {
    id: "patterns",
    title: "Patterns & Trends",
    subtitle: "Discover what affects your glucose",
    icon: CalendarDays,
    reports: [
      {
        title: "Data Overview",
        sidebarTitle: "Year Overview",
        description: "Multi-year heatmap of all your data",
        href: "/reports/year-overview",
        scopes: [READ.glucose],
        icon: CalendarDays,
        status: "available",
      },
      {
        title: "Day-by-Day View",
        sidebarTitle: "Readings",
        description: "Review each day individually",
        href: "/reports/readings",
        scopes: [READ.glucose],
        icon: Calendar,
        status: "available",
      },
      {
        title: "Day in Review",
        description: "Detailed breakdown of a single day",
        href: "/reports/day-in-review",
        scopes: [READ.glucose, READ.treatments, READ.reports],
        icon: Clock,
        status: "available",
      },
      {
        title: "Week to Week",
        description: "Compare patterns across days",
        href: "/reports/week-to-week",
        scopes: [READ.glucose, READ.reports],
        icon: Sunrise,
        status: "available",
      },
      {
        title: "Month to Month",
        description: "Monthly trends and comparisons",
        href: "/reports/month-to-month",
        scopes: [READ.glucose],
        icon: Calendar,
        status: "available",
      },
      {
        title: "Comparison",
        description: "Diff two date ranges side-by-side",
        href: "/reports/comparison",
        scopes: [READ.reports],
        icon: ArrowLeftRight,
        status: "available",
      },
      {
        title: "Hourly Patterns",
        description: "Find your best and worst hours",
        href: "/reports/hourly-stats",
        scopes: [READ.reports],
        icon: Clock,
        status: "coming-soon",
      },
    ],
  },
  {
    id: "lifestyle",
    title: "Lifestyle Impact",
    subtitle: "How food, exercise & sleep affect you",
    icon: HeartPulse,
    reports: [
      {
        title: "Step Count",
        sidebarTitle: "Steps",
        description: "Daily step patterns and activity levels",
        href: "/reports/steps",
        scopes: [READ.glucose, READ.stepCount],
        icon: Footprints,
        status: "available",
      },
      {
        title: "Heart Rate",
        description: "Heart rate patterns and resting estimates",
        href: "/reports/heart-rate",
        scopes: [READ.glucose, READ.heartRate],
        icon: HeartPulse,
        status: "available",
      },
      {
        title: "Sleep & Overnight",
        sidebarTitle: "Sleep",
        description: "Understand your overnight patterns",
        href: "/reports/sleep",
        scopes: [READ.glucose, READ.sleep],
        icon: Moon,
        status: "available",
      },
      {
        title: "Meal Analysis",
        description: "See how different meals affect you",
        href: "/reports/meals",
        scopes: [READ.treatments, READ.food],
        icon: Utensils,
        status: "coming-soon",
      },
      {
        title: "Exercise Impact",
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
    title: "Treatment Insights",
    subtitle: "Is your treatment working?",
    icon: Syringe,
    reports: [
      {
        title: "Treatment Log",
        sidebarTitle: "Treatments",
        description: "Your insulin and carb history",
        href: "/reports/treatments",
        scopes: [READ.glucose, READ.treatments, READ.devices, READ.reports],
        icon: FileText,
        status: "available",
      },
      {
        title: "Basal Rate Analysis",
        sidebarTitle: "Basal Analysis",
        description: "How your basal rates vary",
        href: "/reports/basal-analysis",
        scopes: [READ.reports],
        icon: Layers,
        status: "available",
      },
      {
        title: "Insulin Delivery",
        description: "Basal vs bolus breakdown",
        href: "/reports/insulin-delivery",
        scopes: [READ.reports],
        icon: PieChart,
        status: "available",
      },
      {
        title: "Site Change Impact",
        description: "How site changes affect control",
        href: "/reports/site-change-impact",
        scopes: [READ.devices, READ.reports],
        icon: SiteChangeIcon,
        status: "available",
      },
      {
        title: "Insulin Dosing Profile",
        sidebarTitle: "IDP",
        description: "Standardised insulin and glucose summary",
        href: "/reports/idp",
        scopes: [READ.treatments, READ.reports],
        icon: Syringe,
        status: "available",
        memberOnly: true,
      },
      {
        title: "Battery",
        description: "Pump battery trends and longevity",
        href: "/reports/battery",
        scopes: [READ.devices],
        icon: BatteryFull,
        status: "available",
      },
    ],
  },
];

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
  return reportCategories
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

/** Flat list of available report items for sidebar navigation, per viewer. */
export function getSidebarReportItems(viewer: ReportViewer): {
  title: string;
  href: string;
  icon: IconComponent;
}[] {
  return visibleReportCategories(viewer)
    .flatMap((c) => c.reports)
    .filter((r) => r.status === "available")
    .map((r) => ({
      title: r.sidebarTitle ?? r.title,
      href: r.href,
      icon: r.icon,
    }));
}
