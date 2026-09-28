/**
 * The sidebar's navigation, and who sees which of it.
 *
 * Built here rather than in AppSidebar so the entries and the rules that trim them are one
 * source of truth: a title the trims key on cannot be renamed in the component without the
 * rules moving with it.
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

export interface NavItem {
  title: string;
  href?: string;
  icon: IconComponent;
  strict?: boolean;
  isActive?: boolean;
  children?: NavItem[];
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

/** Titles a guest link session keeps. */
const GUEST_NAV_TITLES: readonly string[] = [
  "Dashboard",
  "Calendar",
  "Time Spans",
  "Reports",
  "Clock",
];

/** Titles the public share view keeps, each with the read scope its pages need. */
const PUBLIC_SHARE_NAV: readonly { title: string; scope?: string }[] = [
  { title: "Dashboard" },
  { title: "Reports", scope: "reports.read" },
];

/**
 * The navigation a read-only viewer keeps, or `null` when the viewer is a member and gets the
 * full navigation.
 *
 * Two read-only viewers reach the app shell: a guest link session, and the public share view.
 * Neither can open anything that writes — those pages land on the login page — and the share is
 * narrower still, holding only the read categories its owner opted into, so a surface is offered
 * only when the share's grant covers it.
 */
function readOnlyNav(items: NavItem[], viewer: NavViewer): NavItem[] | null {
  if (viewer.isGuestSession) {
    const titles = new Set(GUEST_NAV_TITLES);
    return items.filter((item) => titles.has(item.title));
  }

  if (viewer.user) return null;

  const titles = new Set(
    PUBLIC_SHARE_NAV.filter(
      (entry) => !entry.scope || satisfiesScope(viewer.grantedScopes, entry.scope)
    ).map((entry) => entry.title)
  );
  return items.filter((item) => titles.has(item.title));
}

export function buildAppNavigation(viewer: NavViewer): NavItem[] {
  const items: NavItem[] = [
    {
      title: "Dashboard",
      href: "/",
      icon: Home,
      strict: true,
    },
    {
      title: "Calendar",
      href: "/calendar",
      icon: Calendar,
    },
    {
      title: "Time Spans",
      href: "/time-spans",
      icon: Layers,
    },
    {
      title: "Reports",
      icon: BarChart3,
      children: [
        { title: "Overview", href: "/reports", icon: PieChart, strict: true },
        ...getSidebarReportItems({
          grantedScopes: viewer.grantedScopes,
          anonymous: !viewer.user,
        }),
      ],
    },
    {
      title: "Clock",
      href: "/clock",
      icon: Clock,
    },
  ];

  const readOnly = readOnlyNav(items, viewer);
  if (readOnly) return readOnly;

  if (viewer.tenantCount > 1) {
    items.push({
      title: "Tenants",
      href: "/tenants",
      icon: Users,
    });
  }

  items.push(
    {
      title: "Food",
      href: "/food",
      icon: Apple,
    },
    {
      title: "Meals",
      href: "/meals",
      icon: Utensils,
    },
    {
      title: "Tools",
      icon: Wrench,
      children: [{ title: "Packing", href: "/tools/packing", icon: Wrench }],
    }
  );

  items.push(
    {
      title: "Alerts",
      icon: Bell,
      children: [
        { title: "Rules", href: "/alerts", icon: Bell, strict: true },
        { title: "Simulator", href: "/alerts/simulator", icon: PlayCircle },
        { title: "Do Not Disturb", href: "/alerts/dnd", icon: BellOff },
        { title: "History", href: "/alerts/history", icon: HistoryIcon },
      ],
    },
    {
      title: "Dev Tools",
      icon: Terminal,
      children: [
        {
          title: "Compatibility",
          href: "/compatibility",
          icon: CheckCircle,
          strict: true,
        },
        {
          title: "Test Endpoint Compatibility",
          href: "/compatibility/test",
          icon: TestTube,
        },
      ],
    },
    {
      title: "Settings",
      icon: Settings,
      children: [
        { title: "Setup", href: "/setup", icon: ListChecks },
        { title: "Account", href: "/settings/account", icon: User },
        {
          title: "Patient Record",
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
          title: "Data Quality",
          href: "/settings/data-quality",
          icon: ShieldCheck,
        },
        {
          title: "Notifications & Trackers",
          href: "/settings/trackers",
          icon: Timer,
        },
        { title: "Active Access", href: "/settings/access", icon: KeyRound },
        { title: "Connectors & Apps", href: "/settings/connectors", icon: Plug },
        { title: "Sharing & Privacy", href: "/settings/members", icon: Users },
        {
          title: "Support & Community",
          href: "/settings/support",
          icon: HeartHandshake,
        },
        ...(viewer.isPlatformAdmin
          ? [
              { title: "Tenant Management", href: "/settings/admin/tenants", icon: Building2 },
            ]
          : []),
      ],
    }
  );

  // See tenantless-navigation for why the tenant-scoped pages come out.
  if (viewer.tenantless) {
    return filterTenantlessNav(items);
  }

  return items;
}
