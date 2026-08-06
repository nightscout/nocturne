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
   * The report reads a data category that can never be granted to a public share link
   * (sleep, therapy settings — see the API's `TenantPermissions.PublicShareScopes`), so it
   * is hidden from anonymous share viewers rather than rendered as a guaranteed error.
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
        icon: Gauge,
        status: "available",
      },
      {
        title: "Glucose Profile (AGP)",
        sidebarTitle: "AGP",
        description: "Your typical day's glucose pattern",
        href: "/reports/agp",
        icon: BarChart3,
        status: "available",
      },
      {
        title: "Glucose Distribution",
        description: "Time spent in each glucose zone",
        href: "/reports/glucose-distribution",
        icon: PieChart,
        status: "available",
      },
      {
        title: "Data Quality",
        description: "Assess the reliability of your data",
        href: "/reports/data-quality",
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
        icon: CalendarDays,
        status: "available",
      },
      {
        title: "Day-by-Day View",
        sidebarTitle: "Readings",
        description: "Review each day individually",
        href: "/reports/readings",
        icon: Calendar,
        status: "available",
      },
      {
        title: "Day in Review",
        description: "Detailed breakdown of a single day",
        href: "/reports/day-in-review",
        icon: Clock,
        status: "available",
      },
      {
        title: "Week to Week",
        description: "Compare patterns across days",
        href: "/reports/week-to-week",
        icon: Sunrise,
        status: "available",
      },
      {
        title: "Month to Month",
        description: "Monthly trends and comparisons",
        href: "/reports/month-to-month",
        icon: Calendar,
        status: "available",
      },
      {
        title: "Comparison",
        description: "Diff two date ranges side-by-side",
        href: "/reports/comparison",
        icon: ArrowLeftRight,
        status: "available",
      },
      {
        title: "Hourly Patterns",
        description: "Find your best and worst hours",
        href: "/reports/hourly-stats",
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
        icon: Footprints,
        status: "available",
      },
      {
        title: "Heart Rate",
        description: "Heart rate patterns and resting estimates",
        href: "/reports/heart-rate",
        icon: HeartPulse,
        status: "available",
      },
      {
        title: "Sleep & Overnight",
        sidebarTitle: "Sleep",
        description: "Understand your overnight patterns",
        href: "/reports/sleep",
        icon: Moon,
        status: "available",
        memberOnly: true,
      },
      {
        title: "Meal Analysis",
        description: "See how different meals affect you",
        href: "/reports/meals",
        icon: Utensils,
        status: "coming-soon",
      },
      {
        title: "Exercise Impact",
        description: "Track activity's effect on glucose",
        href: "/reports/exercise",
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
        icon: FileText,
        status: "available",
      },
      {
        title: "Basal Rate Analysis",
        sidebarTitle: "Basal Analysis",
        description: "How your basal rates vary",
        href: "/reports/basal-analysis",
        icon: Layers,
        status: "available",
      },
      {
        title: "Insulin Delivery",
        description: "Basal vs bolus breakdown",
        href: "/reports/insulin-delivery",
        icon: PieChart,
        status: "available",
      },
      {
        title: "Site Change Impact",
        description: "How site changes affect control",
        href: "/reports/site-change-impact",
        icon: SiteChangeIcon,
        status: "available",
      },
      {
        title: "Insulin Dosing Profile",
        sidebarTitle: "IDP",
        description: "Standardised insulin and glucose summary",
        href: "/reports/idp",
        icon: Syringe,
        status: "available",
        memberOnly: true,
      },
      {
        title: "Battery",
        description: "Pump battery trends and longevity",
        href: "/reports/battery",
        icon: BatteryFull,
        status: "available",
      },
    ],
  },
];

/**
 * Report categories visible to the current viewer. An anonymous viewer (public share link)
 * does not see member-only reports; categories left empty are dropped.
 */
export function visibleReportCategories(anonymous: boolean): ReportCategory[] {
  if (!anonymous) return reportCategories;
  return reportCategories
    .map((c) => ({ ...c, reports: c.reports.filter((r) => !r.memberOnly) }))
    .filter((c) => c.reports.length > 0);
}

/** Flat list of available report items for sidebar navigation, per viewer. */
export function getSidebarReportItems(anonymous = false): {
  title: string;
  href: string;
  icon: IconComponent;
}[] {
  return visibleReportCategories(anonymous)
    .flatMap((c) => c.reports)
    .filter((r) => r.status === "available")
    .map((r) => ({
      title: r.sidebarTitle ?? r.title,
      href: r.href,
      icon: r.icon,
    }));
}
