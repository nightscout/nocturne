/**
 * The sidebar's navigation, and who sees which of it.
 *
 * Built here rather than in AppSidebar so the entries and the rules that trim them are one
 * source of truth: a title the trims key on cannot be renamed in the component without the
 * rules moving with it.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type IconComponent = any;
import {
  Home,
  BarChart3,
  PieChart,
  Settings,
  Clock,
  User,
  Syringe,
  Apple,
  Utensils,
  Bell,
  BellOff,
  HeartHandshake,
  Plug,
  Calendar,
  CheckCircle,
  Terminal,
  TestTube,
  Palette,
  Timer,
  Layers,
  ShieldCheck,
  Building2,
  Wrench,
  HeartPulse,
  ListChecks,
  Users,
  KeyRound,
  PlayCircle,
  History as HistoryIcon,
} from "lucide-svelte";
import { satisfiesScope } from "$lib/authorization/scopes";
import { getSidebarReportItems } from "$lib/navigation/report-navigation";
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
        ...getSidebarReportItems(!viewer.user),
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
