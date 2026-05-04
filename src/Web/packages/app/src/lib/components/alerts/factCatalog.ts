import type { ConditionKind } from "./types";

/**
 * Leaf condition kinds — every {@link ConditionKind} that is *not* a structural
 * wrapper (`composite`/`not`/`sustained`). Leaves are what the picker offers
 * directly; structural wrappers are added via the per-row action menu.
 */
export type LeafKind = Exclude<ConditionKind, "composite" | "not" | "sustained">;

/**
 * Visual grouping shown in the leaf picker. Determines section ordering and
 * the badge tint applied to each fact's icon.
 */
export type FactGroup =
	| "glucose"
	| "insulin"
	| "carbs"
	| "device"
	| "behaviour"
	| "state"
	| "time"
	| "alert";

export interface FactDef {
	kind: LeafKind;
	label: string;
	/** One-line description used in the picker; not shown on the row itself. */
	description: string;
	group: FactGroup;
	/** Lucide icon name. Resolved by the row component to a Lucide glyph. */
	icon: LucideIconName;
}

/**
 * Lucide icon names referenced by {@link FactDef}. Keep this union narrow so
 * unknown names trip the type checker rather than silently rendering nothing.
 */
export type LucideIconName =
	| "droplet"
	| "trending-up"
	| "syringe"
	| "apple"
	| "clock"
	| "alert-triangle"
	| "battery"
	| "battery-low"
	| "smartphone"
	| "fuel"
	| "rotate-ccw"
	| "wifi-off"
	| "pause-circle"
	| "wand-2"
	| "chart-line"
	| "activity"
	| "bell"
	| "bell-off"
	| "calendar-clock"
	| "calendar-days"
	| "moon";

/**
 * Authoritative leaf-fact catalogue. Order within each group is the order
 * shown in the picker.
 */
export const LEAF_FACTS: readonly FactDef[] = [
	// Glucose
	{ kind: "threshold", label: "Glucose", description: "Current CGM reading vs. a threshold", group: "glucose", icon: "droplet" },
	{ kind: "glucose_bucket", label: "Glucose bucket", description: "Current reading falls in selected buckets (very-low … very-high)", group: "glucose", icon: "chart-line" },
	{ kind: "predicted", label: "Predicted glucose", description: "oref/AAPS forecast crossing a threshold", group: "glucose", icon: "trending-up" },
	{ kind: "rate_of_change", label: "Rate of change", description: "Glucose rising or falling faster than a rate", group: "glucose", icon: "trending-up" },
	{ kind: "trend", label: "Trend", description: "Coarse direction bucket (rising fast, falling, …)", group: "glucose", icon: "trending-up" },
	{ kind: "staleness", label: "Sensor stale", description: "No CGM reading for N minutes", group: "glucose", icon: "alert-triangle" },

	// Insulin
	{ kind: "iob", label: "IOB", description: "Insulin on board", group: "insulin", icon: "syringe" },
	{ kind: "temp_basal", label: "Temp basal", description: "Active temp basal rate or % of scheduled", group: "insulin", icon: "syringe" },
	{ kind: "sensitivity_ratio", label: "Sensitivity", description: "Autosens ratio (AAPS / Trio only)", group: "insulin", icon: "chart-line" },

	// Carbs
	{ kind: "cob", label: "COB", description: "Carbs on board", group: "carbs", icon: "apple" },
	{ kind: "time_since_last_carb", label: "Time since last carb", description: "Minutes elapsed since the last recorded carb entry", group: "carbs", icon: "clock" },
	{ kind: "time_since_last_bolus", label: "Time since last bolus", description: "Minutes elapsed since the last recorded bolus", group: "insulin", icon: "clock" },

	// Device
	{ kind: "reservoir", label: "Reservoir", description: "Pump reservoir level (units)", group: "device", icon: "fuel" },
	{ kind: "site_age", label: "Site age", description: "Hours since last infusion site change", group: "device", icon: "calendar-clock" },
	{ kind: "sensor_age", label: "Sensor age", description: "Days since CGM sensor start", group: "device", icon: "calendar-clock" },
	{ kind: "pump_battery", label: "Pump battery", description: "Pump battery percent", group: "device", icon: "battery" },
	{ kind: "uploader_battery", label: "Phone battery", description: "Uploader phone battery percent", group: "device", icon: "smartphone" },
	{ kind: "loop_stale", label: "Loop has stopped", description: "No APS cycle for N minutes", group: "device", icon: "wifi-off" },
	{ kind: "loop_enaction_stale", label: "Loop not enacting", description: "No enacted cycle for N minutes (closed loop)", group: "device", icon: "rotate-ccw" },
	{ kind: "signal_loss", label: "Signal loss", description: "No CGM data received for N minutes", group: "device", icon: "wifi-off" },

	// Behaviour
	{ kind: "pump_suspended", label: "Pump suspended", description: "Pump suspension state", group: "behaviour", icon: "pause-circle" },
	{ kind: "override_active", label: "Override active", description: "An override profile is active", group: "behaviour", icon: "wand-2" },
	{ kind: "do_not_disturb", label: "Do Not Disturb", description: "Tenant DND state (manual or scheduled)", group: "behaviour", icon: "bell-off" },
	{ kind: "pump_state", label: "Pump mode", description: "Pump operational mode (Automatic, Manual, Boost, …)", group: "behaviour", icon: "activity" },

	// State spans
	{ kind: "state_span_active", label: "State active", description: "Generic state span (override, sleep, exercise, …)", group: "state", icon: "activity" },

	// Time
	{ kind: "time_of_day", label: "Time of day", description: "Current local time falls in a window", group: "time", icon: "clock" },
	{ kind: "day_of_week", label: "Day of week", description: "Current local day matches selected weekdays", group: "time", icon: "calendar-days" },

	// Alert
	{ kind: "alert_state", label: "Other rule state", description: "Reference another alert rule's state", group: "alert", icon: "bell" },
];

const FACT_BY_KIND = new Map<LeafKind, FactDef>(LEAF_FACTS.map((f) => [f.kind, f]));

/** Lookup helper — returns `undefined` when the kind is unknown. */
export function getFact(kind: LeafKind): FactDef | undefined {
	return FACT_BY_KIND.get(kind);
}

/** True iff <paramref name="kind"/> is a leaf (i.e. has a corresponding {@link FactDef}). */
export function isLeafKind(kind: ConditionKind): kind is LeafKind {
	return FACT_BY_KIND.has(kind as LeafKind);
}

/**
 * Visual grouping for the picker, in display order. Group labels live here so
 * the picker's section headings stay aligned with the catalogue.
 */
export const FACT_GROUP_LABELS: Record<FactGroup, string> = {
	glucose: "Glucose",
	insulin: "Insulin",
	carbs: "Carbs",
	device: "Device",
	behaviour: "Behaviour",
	state: "State spans",
	time: "Time",
	alert: "Alerts",
};

/** Ordered list of groups used by the picker. */
export const FACT_GROUP_ORDER: readonly FactGroup[] = [
	"glucose",
	"insulin",
	"carbs",
	"device",
	"behaviour",
	"state",
	"time",
	"alert",
];

/**
 * Tailwind-class pair used to tint a fact's icon and badge. Kept here so the
 * row component is purely presentational. Note: these are deliberately *not*
 * the OKLCH domain colours reserved for actual data renders — they're a
 * navigation cue, similar to the brand's "colored icon badge" motif (15% tint
 * + solid icon).
 */
export const FACT_GROUP_COLOURS: Record<FactGroup, { fg: string; bg: string }> = {
	glucose: { fg: "text-teal-600 dark:text-teal-400", bg: "bg-teal-500/15" },
	insulin: { fg: "text-sky-600 dark:text-sky-400", bg: "bg-sky-500/15" },
	carbs: { fg: "text-orange-600 dark:text-orange-400", bg: "bg-orange-500/15" },
	device: { fg: "text-violet-600 dark:text-violet-400", bg: "bg-violet-500/15" },
	behaviour: { fg: "text-indigo-600 dark:text-indigo-400", bg: "bg-indigo-500/15" },
	state: { fg: "text-fuchsia-600 dark:text-fuchsia-400", bg: "bg-fuchsia-500/15" },
	time: { fg: "text-muted-foreground", bg: "bg-muted" },
	alert: { fg: "text-rose-600 dark:text-rose-400", bg: "bg-rose-500/15" },
};
