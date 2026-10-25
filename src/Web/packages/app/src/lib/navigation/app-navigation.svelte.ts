/**
 * The sidebar's navigation, and who sees which of it.
 *
 * Built here rather than in AppSidebar so the entries and the rules that trim them are one
 * source of truth. The trims key on {@link NavItemId}, never on a title: titles are translated,
 * and are built per call so they are read in the current locale (see `reportCategories`).
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type IconComponent = any;
import Home from "@lucide/svelte/icons/house";
import BarChart3 from "@lucide/svelte/icons/chart-column";
import PieChart from "@lucide/svelte/icons/chart-pie";
import Settings from "@lucide/svelte/icons/settings";
import Clock from "@lucide/svelte/icons/clock";
import User from "@lucide/svelte/icons/user";
import Syringe from "@lucide/svelte/icons/syringe";
import Apple from "@lucide/svelte/icons/apple";
import Utensils from "@lucide/svelte/icons/utensils";
import Bell from "@lucide/svelte/icons/bell";
import BellOff from "@lucide/svelte/icons/bell-off";
import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
import Plug from "@lucide/svelte/icons/plug";
import Calendar from "@lucide/svelte/icons/calendar";
import CheckCircle from "@lucide/svelte/icons/circle-check-big";
import Terminal from "@lucide/svelte/icons/terminal";
import TestTube from "@lucide/svelte/icons/test-tube";
import Palette from "@lucide/svelte/icons/palette";
import Languages from "@lucide/svelte/icons/languages";
import Timer from "@lucide/svelte/icons/timer";
import Layers from "@lucide/svelte/icons/layers";
import ShieldCheck from "@lucide/svelte/icons/shield-check";
import Building2 from "@lucide/svelte/icons/building-2";
import Wrench from "@lucide/svelte/icons/wrench";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import ListChecks from "@lucide/svelte/icons/list-checks";
import Users from "@lucide/svelte/icons/users";
import KeyRound from "@lucide/svelte/icons/key-round";
import PlayCircle from "@lucide/svelte/icons/circle-play";
import HistoryIcon from "@lucide/svelte/icons/history";
import { satisfiesScope } from "$lib/authorization/scopes";
import { getSidebarReportItems } from "$lib/navigation/report-navigation.svelte";
import { filterTenantlessNav } from "$lib/navigation/tenantless-navigation";

/** Stable identity of a top-level sidebar entry, independent of its translated title. */
export type NavItemId =
  | "dashboard"
  | "calendar"
  | "time-spans"
  | "reports"
  | "clock"
  | "tenants"
  | "food"
  | "meals"
  | "tools"
  | "alerts"
  | "dev-tools"
  | "settings";

/** An entry inside a sidebar group; its href is its identity. */
export interface NavLink {
  title: string;
  href: string;
  icon: IconComponent;
  strict?: boolean;
}

export interface NavItem {
  id: NavItemId;
  title: string;
  href?: string;
  icon: IconComponent;
  strict?: boolean;
  children?: NavLink[];
  /** Read scope a member or share must hold to see this entry. A guest link session keeps its `GUEST_NAV` entries regardless. */
  scope?: string;
}

/** The viewer the navigation is built for. */
export interface NavViewer {
  /**
   * The signed-in subject, or null. Inside the authenticated route group the absence of one is
   * the public share view on {token}.share.{baseDomain}: every other anonymous request is
   * redirected to login before the shell renders.
   */
  user: unknown | null;
  /** Whether the session is a guest link session. */
  isGuestSession: boolean;
  /** Whether the viewer administers the platform. */
  isPlatformAdmin: boolean;
  /** The viewer's granted scopes, as `page.data.effectivePermissions` carries them. */
  grantedScopes: readonly string[];
  /** How many tenants the viewer can switch between. */
  tenantCount: number;
  /** Whether this host serves the cross-tenant dashboard rather than one tenant. */
  tenantless: boolean;
}

/** Entries a guest link session keeps. */
const GUEST_NAV: readonly NavItemId[] = ["dashboard", "calendar", "time-spans", "reports"];

/** Entries the public share view keeps, before each is held to its own scope. */
const PUBLIC_SHARE_NAV: readonly NavItemId[] = ["dashboard", "reports"];

/** The entries whose scope, if any, the viewer holds. */
function withinScope(items: NavItem[], viewer: NavViewer): NavItem[] {
  return items.filter((item) => !item.scope || satisfiesScope(viewer.grantedScopes, item.scope));
}

/**
 * The navigation a read-only viewer keeps, or `null` when the viewer is a member and gets the
 * full navigation.
 *
 * Two read-only viewers reach the app shell: a guest link session, and the public share view.
 * Neither can open anything that writes — those pages land on the login page — and the share is
 * narrower still, holding only the read categories its owner opted into, so a surface is offered
 * only when the share's grant covers it.
 */
export function readOnlyNav(items: NavItem[], viewer: NavViewer): NavItem[] | null {
  if (viewer.isGuestSession) {
    return items.filter((item) => GUEST_NAV.includes(item.id));
  }

  if (viewer.user) return null;

  return withinScope(
    items.filter((item) => PUBLIC_SHARE_NAV.includes(item.id)),
    viewer
  );
}

export function buildAppNavigation(viewer: NavViewer): NavItem[] {
  const items: NavItem[] = [
    {
      id: "dashboard",
      title: "Dashboard",
      href: "/",
      icon: Home,
      strict: true,
    },
    {
      id: "calendar",
      title: "Calendar",
      href: "/calendar",
      icon: Calendar,
    },
    {
      id: "time-spans",
      title: "Time spans",
      href: "/time-spans",
      icon: Layers,
    },
    {
      id: "reports",
      title: "Reports",
      icon: BarChart3,
      scope: "reports.read",
      children: [
        { title: "Overview", href: "/reports", icon: PieChart, strict: true },
        ...getSidebarReportItems({
          grantedScopes: viewer.grantedScopes,
          anonymous: !viewer.user,
        }),
      ],
    },
    {
      id: "clock",
      title: "Clock",
      href: "/clock",
      icon: Clock,
    },
  ];

  const readOnly = readOnlyNav(items, viewer);
  if (readOnly) return readOnly;

  if (viewer.tenantCount > 1) {
    items.push({
      id: "tenants",
      title: "Tenants",
      href: "/tenants",
      icon: Users,
    });
  }

  items.push(
    {
      id: "food",
      title: "Food",
      href: "/food",
      icon: Apple,
    },
    {
      id: "meals",
      title: "Meals",
      href: "/meals",
      icon: Utensils,
    },
    {
      id: "tools",
      title: "Tools",
      icon: Wrench,
      children: [{ title: "Packing", href: "/tools/packing", icon: Wrench }],
    }
  );

  items.push(
    {
      id: "alerts",
      title: "Alerts",
      icon: Bell,
      scope: "alerts.read",
      children: [
        { title: "Rules", href: "/alerts", icon: Bell, strict: true },
        { title: "Simulator", href: "/alerts/simulator", icon: PlayCircle },
        { title: "Do not disturb", href: "/alerts/dnd", icon: BellOff },
        { title: "History", href: "/alerts/history", icon: HistoryIcon },
      ],
    },
    {
      id: "dev-tools",
      title: "Dev tools",
      icon: Terminal,
      children: [
        {
          title: "Compatibility",
          href: "/compatibility",
          icon: CheckCircle,
          strict: true,
        },
        {
          title: "Test endpoint compatibility",
          href: "/compatibility/test",
          icon: TestTube,
        },
      ],
    },
    {
      id: "settings",
      title: "Settings",
      icon: Settings,
      children: [
        // The setup hub is the owner's.
        ...(satisfiesScope(viewer.grantedScopes, "*")
          ? [{ title: "Setup", href: "/setup", icon: ListChecks }]
          : []),
        { title: "Account", href: "/settings/account", icon: User },
        {
          title: "Patient record",
          href: "/settings/patient",
          icon: HeartPulse,
        },
        { title: "Appearance", href: "/settings/appearance", icon: Palette },
        {
          title: "Translations",
          href: "/settings/translations",
          icon: Languages,
        },
        { title: "Therapy", href: "/settings/profile", icon: Syringe },
        {
          title: "Data quality",
          href: "/settings/data-quality",
          icon: ShieldCheck,
        },
        {
          title: "Notifications & trackers",
          href: "/settings/trackers",
          icon: Timer,
        },
        { title: "Active access", href: "/settings/access", icon: KeyRound },
        { title: "Connectors & apps", href: "/settings/connectors", icon: Plug },
        { title: "Sharing & privacy", href: "/settings/members", icon: Users },
        {
          title: "Support & community",
          href: "/settings/support",
          icon: HeartHandshake,
        },
        ...(viewer.isPlatformAdmin
          ? [
              { title: "Tenant management", href: "/settings/admin/tenants", icon: Building2 },
            ]
          : []),
      ],
    }
  );

  const visible = withinScope(items, viewer);

  // See tenantless-navigation for why the tenant-scoped pages come out.
  if (viewer.tenantless) {
    return filterTenantlessNav(visible);
  }

  return visible;
}
