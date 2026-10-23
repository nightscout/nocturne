import { a1cLabel } from "$lib/utils/a1c-formatting";
import {
	estimatedA1cTitle,
	reportTitle,
	visibleReportCategories,
	type ReportViewer,
} from "$lib/navigation/report-navigation.svelte";
import type { Component } from "svelte";
import Activity from "@lucide/svelte/icons/activity";
import Apple from "@lucide/svelte/icons/apple";
import Bell from "@lucide/svelte/icons/bell";
import Building from "@lucide/svelte/icons/building";
import CalendarDays from "@lucide/svelte/icons/calendar-days";
import CirclePlus from "@lucide/svelte/icons/circle-plus";
import Clock from "@lucide/svelte/icons/clock";
import Cpu from "@lucide/svelte/icons/cpu";
import Droplet from "@lucide/svelte/icons/droplet";
import FileSearch from "@lucide/svelte/icons/file-search";
import House from "@lucide/svelte/icons/house";
import LifeBuoy from "@lucide/svelte/icons/life-buoy";
import Link from "@lucide/svelte/icons/link";
import Palette from "@lucide/svelte/icons/palette";
import Pill from "@lucide/svelte/icons/pill";
import Plug from "@lucide/svelte/icons/plug";
import Plus from "@lucide/svelte/icons/plus";
import RefreshCw from "@lucide/svelte/icons/refresh-cw";
import Ruler from "@lucide/svelte/icons/ruler";
import Shield from "@lucide/svelte/icons/shield";
import Stethoscope from "@lucide/svelte/icons/stethoscope";
import Sun from "@lucide/svelte/icons/sun";
import Target from "@lucide/svelte/icons/target";
import Timer from "@lucide/svelte/icons/timer";
import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
import User from "@lucide/svelte/icons/user";
import UserPlus from "@lucide/svelte/icons/user-plus";
import Users from "@lucide/svelte/icons/users";
import UtensilsCrossed from "@lucide/svelte/icons/utensils-crossed";
import Wrench from "@lucide/svelte/icons/wrench";
import ZoomIn from "@lucide/svelte/icons/zoom-in";
import { filterTenantlessNav } from "$lib/navigation/tenantless-navigation";

export type CommandPaletteGroup =
	| "stats"
	| "reports"
	| "pages"
	| "settings"
	| "quick-settings"
	| "actions";

export interface CommandPaletteItem {
	id: string;
	label: string;
	group: CommandPaletteGroup;
	description?: string;
	keywords?: string[];
	icon?: Component;
	shortcut?: string;
	/** Scope required to see this item (e.g. "audit.read"). */
	scope?: string;
	/** Shown only to a platform administrator. */
	platformAdmin?: boolean;
	href?: string;
	linkedHref?: string;
}

export const groupMeta: Record<CommandPaletteGroup, { label: string; order: number }> = {
	stats: { label: "Stats", order: 0 },
	reports: { label: "Reports", order: 1 },
	pages: { label: "Pages", order: 2 },
	settings: { label: "Settings", order: 3 },
	"quick-settings": { label: "Quick Settings", order: 4 },
	actions: { label: "Actions", order: 5 },
};

export const items: CommandPaletteItem[] = [
	// ─── Stats ───────────────────────────────────────────────────────────
	{
		id: "stat-iob",
		label: "IOB",
		group: "stats",
		description: "Insulin on board",
		keywords: ["insulin on board", "active insulin"],
		icon: Droplet,
		linkedHref: "/reports/insulin-delivery",
	},
	{
		id: "stat-cob",
		label: "COB",
		group: "stats",
		description: "Carbs on board",
		keywords: ["carbs on board", "active carbs"],
		icon: Pill,
		linkedHref: "/reports/treatments",
	},
	{
		id: "stat-cage",
		label: "CAGE",
		group: "stats",
		description: "Cannula age",
		keywords: ["cannula age", "infusion set", "site age"],
		icon: Timer,
		linkedHref: "/reports/site-change-impact",
	},
	{
		id: "stat-sage",
		label: "SAGE",
		group: "stats",
		description: "Sensor age",
		keywords: ["sensor age", "cgm age"],
		icon: Cpu,
		linkedHref: "/reports/data-quality",
	},
	{
		id: "stat-a1c",
		get label() { return a1cLabel(true); },
		group: "stats",
		get description() { return estimatedA1cTitle(); },
		keywords: ["estimated a1c", "hba1c", "glycated hemoglobin"],
		icon: Activity,
		linkedHref: "/reports/executive-summary",
	},
	{
		id: "stat-tir",
		label: "TIR",
		group: "stats",
		description: "Time in range",
		keywords: ["time in range", "in range", "percentage"],
		icon: Target,
		linkedHref: "/reports/agp",
	},

	// ─── Pages ───────────────────────────────────────────────────────────
	{
		id: "page-dashboard",
		label: "Dashboard",
		group: "pages",
		keywords: ["home", "main", "overview"],
		icon: House,
		href: "/",
	},
	{
		id: "page-food",
		label: "Food",
		group: "pages",
		keywords: ["food", "nutrition", "carbs"],
		icon: Apple,
		href: "/food",
	},
	{
		id: "page-meals",
		label: "Meals",
		group: "pages",
		keywords: ["meals", "eating", "dining"],
		icon: UtensilsCrossed,
		href: "/meals",
	},
	{
		id: "page-calendar",
		label: "Calendar",
		group: "pages",
		keywords: ["calendar", "schedule", "dates", "monthly"],
		icon: CalendarDays,
		href: "/calendar",
	},
	{
		id: "page-tools",
		label: "Tools",
		group: "pages",
		keywords: ["tools", "utilities"],
		icon: Wrench,
		href: "/tools",
	},
	{
		id: "page-notifications",
		label: "Notifications",
		group: "pages",
		keywords: ["notifications", "alerts", "messages"],
		icon: Bell,
		href: "/notifications",
	},
	{
		id: "page-time-spans",
		label: "Time Spans",
		group: "pages",
		keywords: ["time spans", "ranges", "periods"],
		icon: Clock,
		href: "/time-spans",
	},
	{
		id: "page-tenants",
		label: "Tenant Management",
		group: "settings",
		keywords: ["tenants", "organizations", "accounts", "platform admin"],
		icon: Building,
		href: "/settings/admin/tenants",
		platformAdmin: true,
	},
	{
		id: "page-access-requests",
		label: "Access Requests",
		group: "settings",
		keywords: ["access requests", "join", "approve", "pending users"],
		icon: UserPlus,
		href: "/settings/access-requests",
		platformAdmin: true,
	},

	// ─── Settings ────────────────────────────────────────────────────────
	{
		id: "settings-account",
		label: "Account",
		group: "settings",
		keywords: ["account", "email", "password"],
		icon: User,
		href: "/settings/account",
	},
	{
		id: "settings-profile",
		label: "Profile",
		group: "settings",
		keywords: ["profile", "name", "avatar"],
		icon: User,
		href: "/settings/profile",
	},
	{
		id: "settings-appearance",
		label: "Appearance",
		group: "settings",
		keywords: ["appearance", "theme", "dark mode", "light mode"],
		icon: Palette,
		href: "/settings/appearance",
	},
	{
		id: "settings-alerts",
		label: "Alerts",
		group: "settings",
		keywords: ["alerts", "alarms", "urgent", "warnings", "notifications"],
		icon: TriangleAlert,
		href: "/alerts",
	},
	{
		id: "settings-grants",
		label: "Grants",
		group: "settings",
		keywords: ["grants", "access", "permissions", "sharing"],
		icon: Shield,
		href: "/settings/grants",
		scope: "sharing.manage",
	},
	{
		id: "settings-connectors",
		label: "Connectors",
		group: "settings",
		keywords: ["connectors", "dexcom", "libre", "glooko"],
		icon: Plug,
		href: "/settings/connectors",
	},
	{
		// Kept while the label and href moved on: pinned and recent ids persist in local storage,
		// and one that no longer matches an item is dropped from a user's pins without a word.
		id: "settings-integrations",
		// The parent /settings/integrations has no page of its own; only this child does.
		label: "Discord",
		group: "settings",
		keywords: ["discord", "bot", "chat", "integrations", "alerts"],
		icon: Link,
		href: "/settings/integrations/discord",
	},
	{
		id: "settings-data-quality",
		label: "Data Quality",
		group: "settings",
		keywords: ["data quality", "gaps", "thresholds"],
		icon: Shield,
		href: "/settings/data-quality",
	},
	{
		id: "settings-therapy",
		label: "Therapy",
		group: "settings",
		keywords: ["therapy", "treatment", "insulin type"],
		icon: Stethoscope,
		href: "/settings/therapy",
	},
	{
		id: "settings-patient",
		label: "Patient",
		group: "settings",
		keywords: ["patient", "demographic", "details"],
		icon: User,
		href: "/settings/patient",
	},
	{
		id: "settings-sharing-privacy",
		label: "Sharing & Privacy",
		group: "settings",
		keywords: [
			"sharing",
			"privacy",
			"public",
			"members",
			"users",
			"team",
			"roles",
			"permissions",
			"access control",
			"followers",
			"caregivers",
		],
		icon: Users,
		href: "/settings/members",
	},
	{
		id: "settings-audit",
		label: "Audit",
		group: "settings",
		keywords: ["audit", "log", "history", "changes"],
		icon: FileSearch,
		href: "/settings/audit",
		scope: "audit.read",
	},
	{
		id: "settings-support",
		label: "Support",
		group: "settings",
		keywords: ["support", "help", "contact"],
		icon: LifeBuoy,
		href: "/settings/support",
	},

	// ─── Quick Settings ──────────────────────────────────────────────────
	{
		id: "qs-dark-mode",
		label: "Toggle Dark Mode",
		group: "quick-settings",
		description: "Switch between light and dark themes",
		keywords: ["dark mode", "light mode", "theme", "toggle"],
		icon: Sun,
	},
	{
		id: "qs-glucose-units",
		label: "Switch Glucose Units",
		group: "quick-settings",
		description: "Toggle between mg/dL and mmol/L",
		keywords: ["glucose units", "mg/dl", "mmol/l", "units"],
		icon: Ruler,
	},
	{
		id: "qs-chart-lookback",
		label: "Change Chart Lookback",
		group: "quick-settings",
		description: "Adjust the time window for charts",
		keywords: ["lookback", "time range", "chart period", "hours"],
		icon: ZoomIn,
	},

	// ─── Actions ─────────────────────────────────────────────────────────
	{
		id: "action-add-treatment",
		label: "Add Treatment",
		group: "actions",
		description: "Log a new treatment entry",
		keywords: ["add treatment", "log", "bolus", "carbs"],
		icon: Plus,
	},
	{
		id: "action-add-food",
		label: "Add Food Entry",
		group: "actions",
		description: "Add a food item to the database",
		keywords: ["add food", "nutrition", "new food"],
		icon: CirclePlus,
	},
	{
		id: "action-manual-sync",
		label: "Manual Sync",
		group: "actions",
		description: "Trigger a manual data sync",
		keywords: ["sync", "refresh", "fetch", "update"],
		icon: RefreshCw,
	},
];

function reportItems(viewer: ReportViewer): CommandPaletteItem[] {
	return visibleReportCategories(viewer)
		.flatMap((category) => category.reports.map((report) => ({ category, report })))
		.filter(({ report }) => report.status === "available")
		.map(({ category, report }) => ({
			id: `report-${report.href.split("/").pop()}`,
			label: reportTitle(category, report),
			group: "reports",
			description: report.description,
			keywords: report.keywords,
			icon: report.icon,
			href: report.href,
		}));
}

/**
 * The entries a host can actually use. Narrowed on a tenantless host from the same list of hrefs
 * the sidebar narrows from, rather than a second one. See tenantless-navigation.
 */
export function paletteItemsFor(tenantless: boolean, viewer: ReportViewer): CommandPaletteItem[] {
	const all = [...items, ...reportItems(viewer)];
	return tenantless ? filterTenantlessNav(all) : all;
}
