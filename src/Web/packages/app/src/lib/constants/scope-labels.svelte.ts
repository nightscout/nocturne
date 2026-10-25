import { a1cLabel } from "$lib/utils/a1c-formatting";
import type { OAuthScope, PermissionAtom } from "../api/generated/nocturne-api-client";

export type Scope = `${OAuthScope}` | `${PermissionAtom}`;

export type ScopeCategoryGroup = "record" | "therapy" | "account" | "administration" | "audit" | "oauth";

/**
 * A category groups the scopes one row of a permission picker or summary grants. `read` and
 * `readwrite` are the two levels of a data category, `full` the single scope of a toggle.
 * `alsoGrants` lists scopes that holding `full` implies. The `oauth` group is scopes that only
 * OAuth clients and tokens hold, never a role.
 */
export interface ScopeCategory {
  label: string;
  description: string;
  group: ScopeCategoryGroup;
  read?: Scope;
  readwrite?: Scope;
  full?: Scope;
  alsoGrants?: Scope[];
}

/** Built per call, as `reportCategories` is. */
export function scopeLabels(): Record<Scope, string> {
  return {
    "glucose.read": "View glucose readings",
    "glucose.readwrite": "View, record and remove glucose readings",
    "treatments.read": "View treatments",
    "treatments.readwrite": "View, record and remove treatments",
    "devices.read": "View device status",
    "devices.readwrite": "View, update and remove device status",
    "therapy.read": "View treatment profile",
    "therapy.readwrite": "View, update and remove treatment profile",
    "alerts.read": "View alerts",
    "alerts.readwrite": "Manage alerts",
    "alerts.acknowledge": "Silence an alarm for all members for a time",
    "reports.read": "View reports and analytics",
    "identity.read": "View basic account info",
    "sharing.readwrite": "Manage sharing settings",
    "heartrate.read": "View heart rate data",
    "heartrate.readwrite": "View, record and remove heart rate data",
    "stepcount.read": "View step count data",
    "stepcount.readwrite": "View, record and remove step count data",
    "sleep.read": "View sleep data",
    "sleep.readwrite": "View, record and remove sleep data",
    "food.read": "View food data",
    "food.readwrite": "View, record and remove food data",
    "health.read": "View all health data (read-only)",
    "health.readwrite": "View, update and remove all health data",
    "device.notify": "Send notifications to this device, and mute an alert for yourself from it",
    "device.actuate":
      "Use this device's alarms: sound, vibration, flashlight, and full-screen alerts",
    "roles.manage": "Create, edit and delete roles",
    "members.invite": "Create and send invite links",
    "members.manage": "Edit member roles and remove members",
    "tenant.settings": "Change site name, units and preferences",
    "sharing.manage": "Manage public sharing and followers",
    "sharing.guest": "Create and revoke temporary guest access links",
    "audit.read": "Read the history of data changes",
    "audit.manage": "Configure audit retention and export",
    "*": "Full access to all data and settings, including deletion",
  };
}

export function scopeCategories(): ScopeCategory[] {
  const labels = scopeLabels();
  return [
    {
      label: "Blood glucose",
      description: "CGM readings and manual blood glucose entries",
      group: "record",
      read: "glucose.read",
      readwrite: "glucose.readwrite",
    },
    {
      label: "Treatments",
      description: "Insulin doses, carb entries, and therapy events",
      group: "record",
      read: "treatments.read",
      readwrite: "treatments.readwrite",
    },
    {
      label: "Device status",
      description: "Pump, CGM, and phone status reports",
      group: "record",
      read: "devices.read",
      readwrite: "devices.readwrite",
    },
    {
      label: "Heart rate",
      description: "Heart rate data from wearables",
      group: "record",
      read: "heartrate.read",
      readwrite: "heartrate.readwrite",
    },
    {
      label: "Step count",
      description: "Daily step counts from activity trackers",
      group: "record",
      read: "stepcount.read",
      readwrite: "stepcount.readwrite",
    },
    {
      label: "Sleep",
      description: "Sleep sessions, stages, and overnight biometrics",
      group: "record",
      read: "sleep.read",
      readwrite: "sleep.readwrite",
    },
    {
      label: "Food & meals",
      description: "Food database entries and nutritional information",
      group: "record",
      read: "food.read",
      readwrite: "food.readwrite",
    },
    {
      label: "Reports",
      description: `Generated reports and data exports, and statistics: time in range, ${a1cLabel(true)} estimates, and averages`,
      group: "record",
      read: "reports.read",
    },
    {
      label: "Treatment profile",
      description: "Basal rates, sensitivity factors, carb ratios, and targets",
      group: "therapy",
      read: "therapy.read",
      readwrite: "therapy.readwrite",
    },
    {
      label: "Alerts",
      description: "Alert rules, notification delivery, and history",
      group: "therapy",
      read: "alerts.read",
      readwrite: "alerts.readwrite",
    },
    {
      label: "Silence alarms",
      description: labels["alerts.acknowledge"],
      group: "therapy",
      full: "alerts.acknowledge",
    },
    {
      label: "Identity",
      description: "Display name, email, and account details",
      group: "account",
      read: "identity.read",
    },
    {
      label: "Manage roles",
      description: labels["roles.manage"],
      group: "administration",
      full: "roles.manage",
    },
    {
      label: "Invite members",
      description: labels["members.invite"],
      group: "administration",
      full: "members.invite",
    },
    {
      label: "Manage members",
      description: labels["members.manage"],
      group: "administration",
      full: "members.manage",
    },
    {
      label: "Site settings",
      description: labels["tenant.settings"],
      group: "administration",
      full: "tenant.settings",
    },
    {
      label: "Manage sharing",
      description: labels["sharing.manage"],
      group: "administration",
      full: "sharing.manage",
    },
    {
      label: "Guest links",
      description: labels["sharing.guest"],
      group: "administration",
      full: "sharing.guest",
    },
    {
      label: "View audit logs",
      description: labels["audit.read"],
      group: "audit",
      full: "audit.read",
    },
    {
      label: "Manage audit settings",
      description: labels["audit.manage"],
      group: "audit",
      full: "audit.manage",
      alsoGrants: ["audit.read"],
    },
    {
      label: "All health data",
      description: "Covers all the health categories",
      group: "oauth",
      read: "health.read",
      readwrite: "health.readwrite",
    },
    {
      label: "Sharing",
      description: labels["sharing.readwrite"],
      group: "oauth",
      full: "sharing.readwrite",
    },
    {
      label: "All permissions",
      description: labels["*"],
      group: "oauth",
      full: "*",
    },
  ];
}
