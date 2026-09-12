import type { ComponentType } from "svelte";
import {
  Building2,
  HeartHandshake,
  HeartPulse,
  KeyRound,
  ListChecks,
  Palette,
  Plug,
  Shield,
  ShieldCheck,
  Syringe,
  Timer,
  User,
  UserPlus,
  Users,
} from "lucide-svelte";

/** One destination on the settings index, and anywhere else that links to a settings page. */
export type SettingsLink = {
  title: string;
  description: string;
  href: string;
  icon: ComponentType;
};

/**
 * Named where another page links to the same destination, so a retitled page cannot say one thing
 * on the index and another wherever else it is offered.
 */
export const connectorsLink: SettingsLink = {
  title: "Connectors & Apps",
  description: "Connect data sources and authorized devices.",
  href: "/settings/connectors",
  icon: Plug,
};

/** @see {@link connectorsLink} */
export const sharingLink: SettingsLink = {
  title: "Sharing & Privacy",
  description: "Members, invitations, and public sharing.",
  href: "/settings/members",
  icon: Users,
};

/** Mirrors the Settings group in the sidebar so both stay in sync. */
export const settingsSections: SettingsLink[] = [
  {
    title: "Account",
    description: "Profile, passkeys, authenticator apps, and recovery codes.",
    href: "/settings/account",
    icon: User,
  },
  {
    title: "Patient Record",
    description: "Details for the person being monitored.",
    href: "/settings/patient",
    icon: HeartPulse,
  },
  {
    title: "Appearance",
    description: "Theme, units, and display preferences.",
    href: "/settings/appearance",
    icon: Palette,
  },
  {
    title: "Therapy",
    description: "Targets, basal rates, ratios, and treatment profiles.",
    href: "/settings/profile",
    icon: Syringe,
  },
  {
    title: "Data Quality",
    description: "Data validation, cleanup, and maintenance tools.",
    href: "/settings/data-quality",
    icon: ShieldCheck,
  },
  {
    title: "Notifications & Trackers",
    description: "Alerts, reminders, and tracked events.",
    href: "/settings/trackers",
    icon: Timer,
  },
  {
    title: "Active Access",
    description: "Browser sessions signed in to this account.",
    href: "/settings/access",
    icon: KeyRound,
  },
  connectorsLink,
  sharingLink,
  {
    title: "Support & Community",
    description: "Get help and connect with the community.",
    href: "/settings/support",
    icon: HeartHandshake,
  },
];

export const adminSettingsSections: SettingsLink[] = [
  {
    title: "Administration",
    description: "Identity providers and integrations.",
    href: "/settings/admin",
    icon: Shield,
  },
  {
    title: "Tenant Management",
    description: "Tenant details and platform administrators.",
    href: "/settings/admin/tenants",
    icon: Building2,
  },
  {
    title: "Access Requests",
    description: "Review people asking to join this instance.",
    href: "/settings/access-requests",
    icon: UserPlus,
  },
];

export const onboardingSection: SettingsLink = {
  title: "Setup",
  description: "Re-run the guided onboarding checklist.",
  href: "/setup",
  icon: ListChecks,
};
