import { a1cLabel } from "$lib/utils/a1c-formatting";

export interface AccessLevel {
  value: string;
  label: string;
  atoms: string[];
}

export interface PermissionCategory {
  name: string;
  description?: string;
  isSubItem?: boolean;
  levels: AccessLevel[];
}

export interface PermissionGroup {
  header?: string;
  categories: PermissionCategory[];
}

function rw(domain: string): AccessLevel[] {
  return [
    { value: "none", label: "No access", atoms: [] },
    { value: "read", label: "Read", atoms: [`${domain}.read`] },
    {
      value: "readwrite",
      label: "Read & Write",
      atoms: [`${domain}.read`, `${domain}.readwrite`],
    },
  ];
}

function readOnly(domain: string): AccessLevel[] {
  return [
    { value: "none", label: "No access", atoms: [] },
    { value: "read", label: "Read", atoms: [`${domain}.read`] },
  ];
}

function toggle(atom: string): AccessLevel[] {
  return [
    { value: "none", label: "No access", atoms: [] },
    { value: "full", label: "Full access", atoms: [atom] },
  ];
}

export const permissionGroups: PermissionGroup[] = [
  {
    header: "Patient Record",
    categories: [
      { name: "Blood Glucose", description: "CGM readings and manual blood glucose entries", levels: rw("glucose") },
      { name: "Treatments", description: "Insulin doses, carb entries, and therapy events", levels: rw("treatments") },
      { name: "Device Status", description: "Pump, CGM, and phone status reports", levels: rw("devices") },
      { name: "Heart Rate", description: "Heart rate data from wearables", levels: rw("heartrate") },
      { name: "Step Count", description: "Daily step counts from activity trackers", levels: rw("stepcount") },
      { name: "Sleep", description: "Sleep sessions, stages, and overnight biometrics", levels: rw("sleep") },
      { name: "Food & Meals", description: "Food database entries and nutritional information", levels: rw("food") },
      { name: "Reports", get description() { return `Generated reports and data exports, and statistics: time in range, ${a1cLabel(true)} estimates, and averages`; }, levels: readOnly("reports") },
    ],
  },
  {
    header: "Therapy Settings",
    categories: [
      { name: "Treatment Profile", description: "Basal rates, sensitivity factors, carb ratios, and targets", levels: rw("therapy") },
      { name: "Alerts", description: "Alert rules, notification delivery, and history", levels: rw("alerts") },
    ],
  },
  {
    header: "Account",
    categories: [
      { name: "Identity", description: "Display name, email, and account details", levels: readOnly("identity") },
    ],
  },
  {
    header: "Administration",
    categories: [
      { name: "Manage Roles", description: "Create, edit, and delete roles", isSubItem: true, levels: toggle("roles.manage") },
      { name: "Invite Members", description: "Create and send invite links", isSubItem: true, levels: toggle("members.invite") },
      { name: "Manage Members", description: "Edit member roles and remove members", isSubItem: true, levels: toggle("members.manage") },
      { name: "Tenant Settings", description: "Site name, units, and preferences", isSubItem: true, levels: toggle("tenant.settings") },
      { name: "Manage Sharing", description: "Public sharing and follower configuration", isSubItem: true, levels: toggle("sharing.manage") },
      { name: "Guest Links", description: "Create and revoke temporary guest access links", isSubItem: true, levels: toggle("sharing.guest") },
    ],
  },
  {
    header: "Audit",
    categories: [
      { name: "View Audit Logs", description: "Read the history of data changes", isSubItem: true, levels: toggle("audit.read") },
      {
        name: "Manage Audit Settings",
        description: "Configure audit retention and export",
        isSubItem: true,
        levels: [
          { value: "none", label: "No access", atoms: [] },
          { value: "full", label: "Full access", atoms: ["audit.manage", "audit.read"] },
        ],
      },
    ],
  },
];
