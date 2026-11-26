import { a1cLabel } from "#lib/utils/a1c-formatting.js";
import { LegacyConsumableKind } from "#lib/api/index.js";
import type { RealtimeStore } from "#lib/stores/realtime-store.svelte.js";
import { formatAge } from "#lib/components/trackers/age.svelte.js";
import { consumableAgeHours } from "./consumable-age";
import {
	getColorScheme,
	getGlucoseUnits,
	glucoseChartLookback,
} from "#lib/stores/appearance-store.svelte.js";
import {
	estimatedA1cTitle,
	reportTitle,
	withEnglishWords,
	visibleReportCategories,
	type ReportViewer,
} from "#lib/navigation/report-navigation.svelte.js";
import type { Component } from "svelte";
import Activity from "@lucide/svelte/icons/activity";
import Apple from "@lucide/svelte/icons/apple";
import Bell from "@lucide/svelte/icons/bell";
import Building from "@lucide/svelte/icons/building";
import MailPlus from "@lucide/svelte/icons/mail-plus";
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
import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
import Languages from "@lucide/svelte/icons/languages";
import Shield from "@lucide/svelte/icons/shield";
import Stethoscope from "@lucide/svelte/icons/stethoscope";
import Sun from "@lucide/svelte/icons/sun";
import Target from "@lucide/svelte/icons/target";
import Timer from "@lucide/svelte/icons/timer";
import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
import User from "@lucide/svelte/icons/user";
import UserPlus from "@lucide/svelte/icons/user-plus";
import Users from "@lucide/svelte/icons/users";
import UsersRound from "@lucide/svelte/icons/users-round";
import UtensilsCrossed from "@lucide/svelte/icons/utensils-crossed";
import Wrench from "@lucide/svelte/icons/wrench";
import ZoomIn from "@lucide/svelte/icons/zoom-in";
import { filterTenantlessNav } from "#lib/navigation/tenantless-navigation.js";

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
	/** Not offered to the demo visitor, whom the API refuses on the page behind it. */
	notForDemoSubject?: boolean;
	href?: string;
	linkedHref?: string;
}

export function groupMeta(): Record<CommandPaletteGroup, { label: string; order: number }> {
	return {
		stats: { label: "Stats", order: 0 },
		reports: { label: "Reports", order: 1 },
		pages: { label: "Pages", order: 2 },
		settings: { label: "Settings", order: 3 },
		"quick-settings": { label: "Quick settings", order: 4 },
		actions: { label: "Actions", order: 5 },
	};
}

const ENGLISH_PALETTE_KEYWORDS = {
	statIob: ["iob", "insulin on board", "active insulin"],
	statCob: ["cob", "carbs on board", "active carbs"],
	statCage: ["cage", "cannula age", "infusion set", "site age"],
	statSage: ["sage", "sensor age", "cgm age"],
	statA1c: ["estimated a1c", "hba1c", "glycated hemoglobin"],
	statTir: ["tir", "time in range", "in range", "percentage"],
	pageDashboard: ["dashboard", "home", "main", "overview"],
	pageFood: ["food", "nutrition", "carbs"],
	pageMeals: ["meals", "eating", "dining"],
	pageCalendar: ["calendar", "schedule", "dates", "monthly"],
	pageTools: ["tools", "utilities"],
	pageNotifications: ["notifications", "alerts", "messages"],
	pageTimeSpans: ["therapy timeline", "time spans", "profile history", "profile changes"],
	pageTrackers: ["trackers", "consumables", "sensor", "site change", "reminders", "appointments"],
	pageTenants: ["tenants", "organizations", "accounts", "platform admin"],
	pageInvitePeople: ["invite", "sign up", "new site", "new tenant", "platform admin"],
	pageAdministration: ["administration", "identity providers", "oidc", "sso", "bots", "telegram"],
	pageAccessRequests: ["access requests", "join", "approve", "pending users"],
	settingsAccount: ["account", "email", "password"],
	settingsTeams: ["teams", "managers", "invite a manager"],
	settingsProfile: ["profile", "name", "avatar"],
	settingsAppearance: ["appearance", "theme", "dark mode", "light mode"],
	settingsAlerts: ["alerts", "alarms", "urgent", "warnings", "notifications"],
	settingsGrants: ["grants", "access", "permissions", "sharing"],
	settingsConnectors: ["connectors", "dexcom", "libre", "glooko"],
	settingsMigration: ["nightscout", "migration", "import", "transition", "mongodb"],
	settingsSite: ["site", "tenant", "name", "display name", "address"],
	settingsIntegrations: ["discord", "bot", "chat", "integrations", "alerts"],
	settingsDataQuality: ["data quality", "gaps", "thresholds"],
	settingsTherapy: ["therapy", "treatment", "insulin type"],
	settingsPatient: ["patient", "demographic", "details"],
	settingsSharingPrivacy: ["sharing", "privacy", "public", "members", "users", "team", "roles", "permissions", "access control", "followers", "caregivers"],
	settingsAudit: ["audit", "log", "history", "changes"],
	settingsSupport: ["support", "help", "contact"],
	pageCommunity: ["community", "github", "discord", "documentation", "foundation"],
	pageTranslations: ["translations", "translate", "language", "locale"],
	qsDarkMode: ["dark mode", "light mode", "theme", "toggle"],
	qsGlucoseUnits: ["glucose units", "mg/dl", "mmol/l", "units", "switch"],
	qsChartLookback: ["lookback", "time range", "chart period", "hours", "change"],
	actionAddTreatment: ["add treatment", "log", "bolus", "carbs"],
	actionAddFood: ["add food", "nutrition", "new food"],
	actionManualSync: ["sync", "refresh", "fetch", "update"],
} satisfies Record<string, string[]>;

export type PaletteKeywordKey = keyof typeof ENGLISH_PALETTE_KEYWORDS;

/**
 * The words a search finds an entry by, in the current locale, keyed by the entry. A search also matches the
 * English words, which `ENGLISH_PALETTE_KEYWORDS` keeps in step with these. Abbreviations are written in
 * capitals, as the interface writes them, so they share their translation with it; the search ignores case.
 */
export function paletteKeywordWords(): Record<PaletteKeywordKey, string[]> {
	return {
		// @wc-include
		statIob: ["IOB", "insulin on board", "active insulin"],
		// @wc-include
		statCob: ["COB", "carbs on board", "active carbs"],
		// @wc-include
		statCage: ["CAGE", "cannula age", "infusion set", "site age"],
		// @wc-include
		statSage: ["SAGE", "sensor age", "CGM age"],
		// @wc-include
		statA1c: ["estimated A1c", "HbA1c", "glycated hemoglobin"],
		// @wc-include
		statTir: ["TIR", "time in range", "in range", "percentage"],
		// @wc-include
		pageDashboard: ["dashboard", "home", "main", "overview"],
		// @wc-include
		pageFood: ["food", "nutrition", "carbs"],
		// @wc-include
		pageMeals: ["meals", "eating", "dining"],
		// @wc-include
		pageCalendar: ["calendar", "schedule", "dates", "monthly"],
		// @wc-include
		pageTools: ["tools", "utilities"],
		// @wc-include
		pageNotifications: ["notifications", "alerts", "messages"],
		// @wc-include
		pageTimeSpans: ["therapy timeline", "time spans", "profile history", "profile changes"],
		// @wc-include
		pageTrackers: ["trackers", "consumables", "sensor", "site change", "reminders", "appointments"],
		// @wc-include
		pageTenants: ["tenants", "organizations", "accounts", "platform admin"],
		// @wc-include
		pageInvitePeople: ["invite", "sign up", "new site", "new tenant", "platform admin"],
		// @wc-include
		pageAdministration: ["administration", "identity providers", "OIDC", "SSO", "bots", "Telegram"],
		// @wc-include
		pageAccessRequests: ["access requests", "join", "approve", "pending users"],
		// @wc-include
		settingsAccount: ["account", "email", "password"],
		// @wc-include
		settingsTeams: ["teams", "managers", "invite a manager"],
		// @wc-include
		settingsProfile: ["profile", "name", "avatar"],
		// @wc-include
		settingsAppearance: ["appearance", "theme", "dark mode", "light mode"],
		// @wc-include
		settingsAlerts: ["alerts", "alarms", "urgent", "warnings", "notifications"],
		// @wc-include
		settingsGrants: ["grants", "access", "permissions", "sharing"],
		// @wc-include
		settingsConnectors: ["connectors", "Dexcom", "Libre", "Glooko"],
		// @wc-include
		settingsMigration: ["Nightscout", "migration", "import", "transition", "MongoDB"],
		// @wc-include
		settingsSite: ["site", "tenant", "name", "display name", "address"],
		// @wc-include
		settingsIntegrations: ["Discord", "bot", "chat", "integrations", "alerts"],
		// @wc-include
		settingsDataQuality: ["data quality", "gaps", "thresholds"],
		// @wc-include
		settingsTherapy: ["therapy", "treatment", "insulin type"],
		// @wc-include
		settingsPatient: ["patient", "demographic", "details"],
		// @wc-include
		settingsSharingPrivacy: ["sharing", "privacy", "public", "members", "users", "team", "roles", "permissions", "access control", "followers", "caregivers"],
		// @wc-include
		settingsAudit: ["audit", "log", "history", "changes"],
		// @wc-include
		settingsSupport: ["support", "help", "contact"],
		// @wc-include
		pageCommunity: ["community", "GitHub", "Discord", "documentation", "foundation"],
		// @wc-include
		pageTranslations: ["translations", "translate", "language", "locale"],
		// @wc-include
		qsDarkMode: ["dark mode", "light mode", "theme", "toggle"],
		// @wc-include
		qsGlucoseUnits: ["glucose units", "mg/dL", "mmol/L", "units", "switch"],
		// @wc-include
		qsChartLookback: ["lookback", "time range", "chart period", "hours", "change"],
		// @wc-include
		actionAddTreatment: ["add treatment", "log", "bolus", "carbs"],
		// @wc-include
		actionAddFood: ["add food", "nutrition", "new food"],
		// @wc-include
		actionManualSync: ["sync", "refresh", "fetch", "update"],
	};
}

/** An entry's search words. */
function paletteKeywords(key: PaletteKeywordKey, translated: Partial<Record<PaletteKeywordKey, string[]>>): string[] {
	return withEnglishWords(translated[key] ?? [], ENGLISH_PALETTE_KEYWORDS[key]);
}

/** The live value each stat shows after its name; absent while the store has none. */
export interface PaletteStatValues {
	iob?: string;
	cob?: string;
	cage?: string;
	sage?: string;
}

/** The stats' live values, read from the realtime store. */
export function paletteStatValues(
	store: Pick<RealtimeStore, "pillsData" | "legacyFallbacks" | "legacyCoverage" | "trackerInstances" | "nowMinute">,
): PaletteStatValues {
	const age = (kind: LegacyConsumableKind) => {
		const hours = consumableAgeHours(kind, store.legacyFallbacks, store.legacyCoverage, store.trackerInstances, store.nowMinute);
		return hours === undefined ? undefined : formatAge(hours);
	};
	return {
		iob: store.pillsData.iob?.display ?? undefined,
		cob: store.pillsData.cob?.display ?? undefined,
		cage: age(LegacyConsumableKind.Site),
		sage: age(LegacyConsumableKind.Sensor),
	};
}

function statLabel(name: string, value: string | undefined): string {
	return value ? `${name}: ${value}` : name;
}

function darkModeState(): string {
	const scheme = getColorScheme();
	return scheme === "dark" ? "On" : scheme === "light" ? "Off" : "System";
}

/**
 * Built per call so the labels and words are read in the current locale: wuchale translates strings
 * inside a function on each call, and a module-level constant only once.
 */
export function paletteItems(
	words: Partial<Record<PaletteKeywordKey, string[]>> = paletteKeywordWords(),
	stats: PaletteStatValues = {},
): CommandPaletteItem[] {
	return [
		// ─── Stats ───────────────────────────────────────────────────────────
		{
			id: "stat-iob",
			label: statLabel("IOB", stats.iob),
			group: "stats",
			description: "Insulin on board",
			keywords: paletteKeywords("statIob", words),
			icon: Droplet,
			linkedHref: "/reports/insulin-delivery",
		},
		{
			id: "stat-cob",
			label: statLabel("COB", stats.cob),
			group: "stats",
			description: "Carbs on board",
			keywords: paletteKeywords("statCob", words),
			icon: Pill,
			linkedHref: "/reports/treatments",
		},
		{
			id: "stat-cage",
			label: statLabel("CAGE", stats.cage),
			group: "stats",
			description: "Cannula age",
			keywords: paletteKeywords("statCage", words),
			icon: Timer,
			linkedHref: "/reports/site-change-impact",
		},
		{
			id: "stat-sage",
			label: statLabel("SAGE", stats.sage),
			group: "stats",
			description: "Sensor age",
			keywords: paletteKeywords("statSage", words),
			icon: Cpu,
			linkedHref: "/reports/data-quality",
		},
		{
			id: "stat-a1c",
			label: a1cLabel(true),
			group: "stats",
			description: estimatedA1cTitle(),
			keywords: paletteKeywords("statA1c", words),
			icon: Activity,
			linkedHref: "/reports/executive-summary",
		},
		{
			id: "stat-tir",
			label: "TIR",
			group: "stats",
			description: "Time in range",
			keywords: paletteKeywords("statTir", words),
			icon: Target,
			linkedHref: "/reports/agp",
		},

		// ─── Pages ───────────────────────────────────────────────────────────
		{
			id: "page-dashboard",
			label: "Dashboard",
			group: "pages",
			keywords: paletteKeywords("pageDashboard", words),
			icon: House,
			href: "/",
		},
		{
			id: "page-food",
			label: "Food",
			group: "pages",
			keywords: paletteKeywords("pageFood", words),
			icon: Apple,
			href: "/food",
		},
		{
			id: "page-meals",
			label: "Meals",
			group: "pages",
			keywords: paletteKeywords("pageMeals", words),
			icon: UtensilsCrossed,
			href: "/meals",
		},
		{
			id: "page-calendar",
			label: "Calendar",
			group: "pages",
			keywords: paletteKeywords("pageCalendar", words),
			icon: CalendarDays,
			href: "/calendar",
		},
		{
			id: "page-tools",
			label: "Tools",
			group: "pages",
			keywords: paletteKeywords("pageTools", words),
			icon: Wrench,
			href: "/tools",
		},
		{
			id: "page-notifications",
			label: "Notifications",
			group: "pages",
			keywords: paletteKeywords("pageNotifications", words),
			icon: Bell,
			href: "/notifications",
		},
		{
			id: "page-time-spans",
			label: "Therapy timeline",
			group: "pages",
			keywords: paletteKeywords("pageTimeSpans", words),
			icon: Clock,
			href: "/time-spans",
		},
		{
			id: "page-trackers",
			label: "Trackers",
			group: "pages",
			keywords: paletteKeywords("pageTrackers", words),
			icon: Timer,
			href: "/trackers",
			scope: "alerts.read",
		},
		{
			id: "page-tenants",
			label: "All tenants",
			group: "settings",
			keywords: paletteKeywords("pageTenants", words),
			icon: Building,
			href: "/settings/admin/tenants",
			platformAdmin: true,
		},
		{
			id: "page-invite-people",
			label: "Invite people",
			group: "settings",
			keywords: paletteKeywords("pageInvitePeople", words),
			icon: MailPlus,
			href: "/settings/admin/invites",
			platformAdmin: true,
		},
		{
			id: "page-administration",
			label: "Administration",
			group: "settings",
			keywords: paletteKeywords("pageAdministration", words),
			icon: Shield,
			href: "/settings/admin",
			platformAdmin: true,
		},
		{
			id: "page-access-requests",
			label: "Access requests",
			group: "settings",
			keywords: paletteKeywords("pageAccessRequests", words),
			icon: UserPlus,
			href: "/settings/access-requests",
			platformAdmin: true,
		},

		// ─── Settings ────────────────────────────────────────────────────────
		{
			id: "settings-account",
			label: "Account",
			group: "settings",
			keywords: paletteKeywords("settingsAccount", words),
			icon: User,
			href: "/settings/account",
		},
		{
			id: "settings-teams",
			label: "Teams",
			group: "settings",
			keywords: paletteKeywords("settingsTeams", words),
			icon: UsersRound,
			href: "/settings/teams",
			notForDemoSubject: true,
		},
		{
			id: "settings-profile",
			label: "Profile",
			group: "settings",
			keywords: paletteKeywords("settingsProfile", words),
			icon: User,
			href: "/settings/profile",
		},
		{
			id: "settings-appearance",
			label: "Appearance",
			group: "settings",
			keywords: paletteKeywords("settingsAppearance", words),
			icon: Palette,
			href: "/settings/appearance",
		},
		{
			id: "settings-alerts",
			label: "Alerts",
			group: "settings",
			keywords: paletteKeywords("settingsAlerts", words),
			icon: TriangleAlert,
			href: "/alerts",
			scope: "alerts.read",
		},
		{
			id: "settings-grants",
			label: "Grants",
			group: "settings",
			keywords: paletteKeywords("settingsGrants", words),
			icon: Shield,
			href: "/settings/grants",
			scope: "sharing.manage",
		},
		{
			id: "settings-connectors",
			label: "Connectors",
			group: "settings",
			keywords: paletteKeywords("settingsConnectors", words),
			icon: Plug,
			href: "/settings/connectors",
		},
		{
			id: "settings-migration",
			label: "Move from Nightscout",
			group: "settings",
			keywords: paletteKeywords("settingsMigration", words),
			icon: RefreshCw,
			href: "/settings/migration",
			scope: "*",
		},
		{
			id: "settings-site",
			label: "Site details",
			group: "settings",
			keywords: paletteKeywords("settingsSite", words),
			icon: Building,
			href: "/settings/site",
			scope: "tenant.settings",
		},
		{
			// Kept while the label and href moved on: pinned and recent ids persist in local storage,
			// and one that no longer matches an item is dropped from a user's pins without a word.
			id: "settings-integrations",
			// The parent /settings/integrations has no page of its own; only this child does.
			label: "Discord",
			group: "settings",
			keywords: paletteKeywords("settingsIntegrations", words),
			icon: Link,
			href: "/settings/integrations/discord",
		},
		{
			id: "settings-data-quality",
			label: "Data quality",
			group: "settings",
			keywords: paletteKeywords("settingsDataQuality", words),
			icon: Shield,
			href: "/settings/data-quality",
		},
		{
			id: "settings-therapy",
			label: "Therapy",
			group: "settings",
			keywords: paletteKeywords("settingsTherapy", words),
			icon: Stethoscope,
			href: "/settings/therapy",
		},
		{
			id: "settings-patient",
			label: "Patient",
			group: "settings",
			keywords: paletteKeywords("settingsPatient", words),
			icon: User,
			href: "/settings/patient",
		},
		{
			id: "settings-sharing-privacy",
			label: "Sharing & privacy",
			group: "settings",
			keywords: paletteKeywords("settingsSharingPrivacy", words),
			icon: Users,
			href: "/settings/members",
		},
		{
			id: "settings-audit",
			label: "Audit",
			group: "settings",
			keywords: paletteKeywords("settingsAudit", words),
			icon: FileSearch,
			href: "/settings/audit",
			scope: "audit.read",
		},
		{
			id: "settings-support",
			label: "Support",
			group: "settings",
			keywords: paletteKeywords("settingsSupport", words),
			icon: LifeBuoy,
			href: "/settings/support",
		},
		{
			id: "page-community",
			label: "Community",
			group: "pages",
			keywords: paletteKeywords("pageCommunity", words),
			icon: HeartHandshake,
			href: "/community",
		},
		{
			id: "page-translations",
			label: "Translations",
			group: "pages",
			keywords: paletteKeywords("pageTranslations", words),
			icon: Languages,
			href: "/community",
		},

		// ─── Quick Settings ──────────────────────────────────────────────────
		{
			id: "qs-dark-mode",
			label: `Dark mode: ${darkModeState()}`,
			group: "quick-settings",
			description: "Switch between light and dark themes",
			keywords: paletteKeywords("qsDarkMode", words),
			icon: Sun,
		},
		{
			id: "qs-glucose-units",
			label: `Units: ${getGlucoseUnits() === "mg/dl" ? "mg/dL" : "mmol/L"}`,
			group: "quick-settings",
			description: "Toggle between mg/dL and mmol/L",
			keywords: paletteKeywords("qsGlucoseUnits", words),
			icon: Ruler,
		},
		{
			id: "qs-chart-lookback",
			label: `Chart lookback: ${glucoseChartLookback.current}h`,
			group: "quick-settings",
			description: "Adjust the time window for charts",
			keywords: paletteKeywords("qsChartLookback", words),
			icon: ZoomIn,
		},

		// ─── Actions ─────────────────────────────────────────────────────────
		{
			id: "action-add-treatment",
			label: "Add treatment",
			group: "actions",
			description: "Log a new treatment entry",
			keywords: paletteKeywords("actionAddTreatment", words),
			icon: Plus,
		},
		{
			id: "action-add-food",
			label: "Add food entry",
			group: "actions",
			description: "Add a food item to the database",
			keywords: paletteKeywords("actionAddFood", words),
			icon: CirclePlus,
		},
		{
			id: "action-manual-sync",
			label: "Manual sync",
			group: "actions",
			description: "Trigger a manual data sync",
			keywords: paletteKeywords("actionManualSync", words),
			icon: RefreshCw,
		},
	];
}

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
export function paletteItemsFor(
	tenantless: boolean,
	viewer: ReportViewer,
	words: Partial<Record<PaletteKeywordKey, string[]>> = paletteKeywordWords(),
	stats: PaletteStatValues = {},
): CommandPaletteItem[] {
	const all = [...paletteItems(words, stats), ...reportItems(viewer)];
	return tenantless ? filterTenantlessNav(all) : all;
}
