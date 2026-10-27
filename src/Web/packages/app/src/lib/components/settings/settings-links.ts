import type { Component } from "svelte";
import Building2 from "@lucide/svelte/icons/building-2";
import DatabaseBackup from "@lucide/svelte/icons/database-backup";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import FileSearch from "@lucide/svelte/icons/file-search";
import KeyRound from "@lucide/svelte/icons/key-round";
import LifeBuoy from "@lucide/svelte/icons/life-buoy";
import ListChecks from "@lucide/svelte/icons/list-checks";
import MessageCircle from "@lucide/svelte/icons/message-circle";
import Palette from "@lucide/svelte/icons/palette";
import Plug from "@lucide/svelte/icons/plug";
import Settings from "@lucide/svelte/icons/settings";
import Shield from "@lucide/svelte/icons/shield";
import ShieldCheck from "@lucide/svelte/icons/shield-check";
import Syringe from "@lucide/svelte/icons/syringe";
import User from "@lucide/svelte/icons/user";
import UserCog from "@lucide/svelte/icons/user-cog";
import UserPlus from "@lucide/svelte/icons/user-plus";
import Users from "@lucide/svelte/icons/users";
import { satisfiesScope } from "#lib/authorization/scopes.js";

/**
 * One destination on the settings index, and anywhere else that links to a
 * settings page.
 */
export type SettingsLink = {
  title: string;
  description: string;
  href: string;
  icon: Component;
  /** Active only on this exact path, not on the pages under it. */
  strict?: boolean;
  /** Scope the viewer must hold to be offered the link. */
  scope?: string;
};

/**
 * Named where another page links to the same destination, so a retitled page
 * cannot say one thing on the index and another wherever else it is offered.
 */
export const connectorsLink: SettingsLink = {
  title: "Connectors & apps",
  description: "Connect data sources and authorized devices.",
  href: "/settings/connectors",
  icon: Plug,
};

/** @see {@link connectorsLink} */
export const sharingLink: SettingsLink = {
  title: "Sharing & privacy",
  description: "Members, invitations, and public sharing.",
  href: "/settings/members",
  icon: Users,
};

export type SettingsGroupId = "my-settings" | "tenant-settings" | "platform";

/** Settings split by whose they are: the person's, the tenant's, or the instance's. */
export interface SettingsGroup {
  id: SettingsGroupId;
  title: string;
  description: string;
  icon: Component;
  links: SettingsLink[];
}

export interface SettingsViewer {
  isPlatformAdmin: boolean;
  /** The viewer's granted scopes, as `page.data.effectivePermissions` carries them. */
  grantedScopes: readonly string[];
}

/**
 * The settings the viewer is offered, grouped. The sidebar and the settings index both render
 * this, so the two cannot drift apart. A group the viewer has no link in is left out.
 */
export function settingsGroups(viewer: SettingsViewer): SettingsGroup[] {
  const groups: SettingsGroup[] = [
    {
      id: "my-settings",
      title: "My settings",
      description: "Your account and how Nocturne looks for you.",
      icon: UserCog,
      links: [
        {
          title: "Account",
          description: "Profile, passkeys, authenticator apps, and recovery codes.",
          href: "/settings/account",
          icon: User,
        },
        {
          title: "Appearance",
          description: "Theme, units, and display preferences.",
          href: "/settings/appearance",
          icon: Palette,
        },
        {
          title: "Active access",
          description: "Browser sessions signed in to this account.",
          href: "/settings/access",
          icon: KeyRound,
        },
        {
          title: "Discord",
          description: "Link your Discord account to the Nocturne bot.",
          href: "/settings/integrations/discord",
          icon: MessageCircle,
        },
        {
          title: "Support",
          description: "Get help, tutorials, and diagnostic logs.",
          href: "/settings/support",
          icon: LifeBuoy,
        },
      ],
    },
    {
      id: "tenant-settings",
      title: "Tenant settings",
      description: "The data, therapy, and access for this tenant.",
      icon: Settings,
      links: [
        {
          title: "Setup",
          description: "Do the guided setup checklist again.",
          href: "/setup",
          icon: ListChecks,
          scope: "*",
        },
        {
          title: "Patient record",
          description: "Details for the person being monitored.",
          href: "/settings/patient",
          icon: HeartPulse,
        },
        {
          title: "Therapy",
          description: "Targets, basal rates, ratios, and treatment profiles.",
          href: "/settings/profile",
          icon: Syringe,
        },
        {
          title: "Data quality",
          description: "Data validation, cleanup, and maintenance tools.",
          href: "/settings/data-quality",
          icon: ShieldCheck,
        },
        connectorsLink,
        sharingLink,
        {
          title: "Audit log",
          description: "Data changes and access history.",
          href: "/settings/audit",
          icon: FileSearch,
          scope: "audit.read",
        },
      ],
    },
    {
      id: "platform",
      title: "Platform",
      description: "Settings for every tenant on this instance.",
      icon: Shield,
      links: viewer.isPlatformAdmin
        ? [
            {
              title: "Administration",
              description: "Identity providers and integrations.",
              href: "/settings/admin",
              icon: Shield,
              strict: true,
            },
            {
              title: "Tenant management",
              description: "Tenant details and platform administrators.",
              href: "/settings/admin/tenants",
              icon: Building2,
            },
            {
              title: "Access requests",
              description: "Review people asking to join this instance.",
              href: "/settings/access-requests",
              icon: UserPlus,
            },
            {
              title: "Connector cursors",
              description: "Pull a tenant's connector history again.",
              href: "/settings/admin/connector-cursors",
              icon: DatabaseBackup,
            },
          ]
        : [],
    },
  ];

  return groups
    .map((group) => ({
      ...group,
      links: group.links.filter(
        (link) => !link.scope || satisfiesScope(viewer.grantedScopes, link.scope)
      ),
    }))
    .filter((group) => group.links.length > 0);
}
