/**
 * The settings pages, grouped by who they belong to, and who sees which of them.
 *
 * The sidebar and the settings index both render from here. Titles are built per call so they are
 * read in the current locale (see `app-navigation.svelte.ts`).
 */
import type { Component } from "svelte";
import Building from "@lucide/svelte/icons/building";
import Building2 from "@lucide/svelte/icons/building-2";
import FileSearch from "@lucide/svelte/icons/file-search";
import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import LifeBuoy from "@lucide/svelte/icons/life-buoy";
import ListChecks from "@lucide/svelte/icons/list-checks";
import MessageCircle from "@lucide/svelte/icons/message-circle";
import Palette from "@lucide/svelte/icons/palette";
import Plug from "@lucide/svelte/icons/plug";
import RefreshCw from "@lucide/svelte/icons/refresh-cw";
import Shield from "@lucide/svelte/icons/shield";
import ShieldCheck from "@lucide/svelte/icons/shield-check";
import Syringe from "@lucide/svelte/icons/syringe";
import User from "@lucide/svelte/icons/user";
import UserPlus from "@lucide/svelte/icons/user-plus";
import Users from "@lucide/svelte/icons/users";
import { satisfiesScope } from "#lib/authorization/scopes.js";

/** One settings destination. */
export type SettingsLink = {
  title: string;
  description: string;
  href: string;
  icon: Component;
  /** Active only on its own path, not on the pages under it. */
  strict?: boolean;
  /** Scopes of which the viewer must hold at least one to be offered the link. */
  anyScope?: readonly string[];
};

/**
 * Who a group belongs to: the signed-in person, the site (tenant) they are a member of, or the
 * platform that hosts every site.
 */
export type SettingsGroupId = "personal" | "site" | "platform";

export interface SettingsGroup {
  id: SettingsGroupId;
  title: string;
  description: string;
  links: SettingsLink[];
}

/** The viewer the settings are offered to. */
export interface SettingsViewer {
  /** The viewer's granted scopes, as `page.data.effectivePermissions` carries them. */
  grantedScopes: readonly string[];
  /** Whether the viewer administers the platform. */
  isPlatformAdmin: boolean;
}

function settingsGroups(): SettingsGroup[] {
  return [
    {
      id: "personal",
      title: "My settings",
      description: "Your sign-in, your display, and your linked chat accounts.",
      links: [
        {
          title: "Account",
          description: "Profile, passkeys, authenticator apps, recovery codes, and sessions.",
          href: "/settings/account",
          icon: User,
        },
        {
          title: "Appearance",
          description: "Theme, units, language, and display preferences.",
          href: "/settings/appearance",
          icon: Palette,
        },
        {
          title: "Discord",
          description: "Link your Discord account to get alerts and data in chat.",
          href: "/settings/integrations/discord",
          icon: MessageCircle,
        },
      ],
    },
    {
      id: "site",
      title: "Site settings",
      description: "Settings for this site and the person it monitors.",
      links: [
        {
          title: "Setup",
          description: "Do the guided setup checklist again.",
          href: "/setup",
          icon: ListChecks,
          anyScope: ["*"],
        },
        {
          title: "Site details",
          description: "The name and address of this site.",
          href: "/settings/site",
          icon: Building,
          anyScope: ["tenant.settings"],
        },
        {
          title: "Patient record",
          description: "Details, weight, and timezone history of the person monitored.",
          href: "/settings/patient",
          icon: HeartPulse,
          anyScope: ["therapy.read"],
        },
        {
          title: "Therapy",
          description: "Targets, basal rates, ratios, and treatment profiles.",
          href: "/settings/profile",
          icon: Syringe,
          anyScope: ["therapy.read"],
        },
        {
          title: "Data quality",
          description: "Sleep schedule, compression low detection, and data cleanup.",
          href: "/settings/data-quality",
          icon: ShieldCheck,
          anyScope: ["alerts.readwrite", "glucose.readwrite"],
        },
        {
          title: "Connectors & apps",
          description: "Data sources, your connected apps and API keys, and the move from Nightscout.",
          href: "/settings/connectors",
          icon: Plug,
        },
        {
          title: "Sharing & privacy",
          description: "Members, invitations, and public sharing.",
          href: "/settings/members",
          icon: Users,
          anyScope: [
            "members.invite",
            "members.manage",
            "roles.manage",
            "sharing.manage",
            "sharing.guest",
          ],
        },
        {
          title: "Audit log",
          description: "Who read and changed the data on this site.",
          href: "/settings/audit",
          icon: FileSearch,
          anyScope: ["audit.read"],
        },
      ],
    },
    {
      id: "platform",
      title: "Platform",
      description: "Settings for every site on this Nocturne server.",
      links: [
        {
          title: "Administration",
          description: "Identity providers and chat platform settings.",
          href: "/settings/admin",
          icon: Shield,
          strict: true,
        },
        {
          title: "All tenants",
          description: "All sites on this server, and platform administrators.",
          href: "/settings/admin/tenants",
          icon: Building2,
        },
        {
          title: "Access requests",
          description: "People who ask to join this server.",
          href: "/settings/access-requests",
          icon: UserPlus,
        },
        {
          title: "Connector cursors",
          description: "Sync a connector again from a time you select.",
          href: "/settings/admin/connector-cursors",
          icon: RefreshCw,
        },
      ],
    },
  ];
}

/** Help and contribution pages. They are not settings, so no group carries them. */
export function helpLinks(): SettingsLink[] {
  return [
    {
      title: "Support",
      description: "Get help, tutorials, and diagnostic logs.",
      href: "/settings/support",
      icon: LifeBuoy,
    },
    {
      title: "Community",
      description: "Project links, and help to translate Nocturne.",
      href: "/community",
      icon: HeartHandshake,
    },
  ];
}

/**
 * The groups and links the viewer is offered. A link shows when the viewer holds one of its
 * scopes, the platform group only to a platform administrator, and a group left with no links is
 * dropped. An offer is not a grant: each page's endpoints still answer on the server's terms.
 */
export function visibleSettingsGroups(viewer: SettingsViewer): SettingsGroup[] {
  return settingsGroups()
    .filter((group) => group.id !== "platform" || viewer.isPlatformAdmin)
    .map((group) => ({
      ...group,
      links: group.links.filter(
        (link) =>
          !link.anyScope ||
          link.anyScope.some((scope) => satisfiesScope(viewer.grantedScopes, scope))
      ),
    }))
    .filter((group) => group.links.length > 0);
}
