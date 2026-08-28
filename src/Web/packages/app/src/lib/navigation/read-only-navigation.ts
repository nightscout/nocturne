/**
 * Navigation filtering for read-only viewers.
 *
 * Two of them reach the app shell: a guest link session, and the public share view on
 * {token}.share.{baseDomain}. Neither can open anything that writes — those pages land on the
 * login page — and the share is narrower still, holding only the read categories its owner
 * opted into, so a surface is offered only when the share's grant covers it.
 */
import { satisfiesScope } from "$lib/authorization/scopes";

interface NavLike {
  title: string;
}

/** The viewer a navigation list is built for. */
export interface NavViewer {
  /** Whether the session is a guest link session. */
  isGuestSession: boolean;
  /**
   * Whether the viewer is a public share link rather than a signed-in member. Inside the
   * authenticated route group the absence of a user is exactly that: every other anonymous
   * request is redirected to login before the shell renders.
   */
  anonymous: boolean;
  /** The viewer's granted scopes, as `page.data.effectivePermissions` carries them. */
  grantedScopes: readonly string[];
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
 */
export function readOnlyNav<T extends NavLike>(
  items: readonly T[],
  viewer: NavViewer
): T[] | null {
  if (viewer.isGuestSession) {
    const titles = new Set(GUEST_NAV_TITLES);
    return items.filter((item) => titles.has(item.title));
  }

  if (!viewer.anonymous) return null;

  const titles = new Set(
    PUBLIC_SHARE_NAV.filter(
      (entry) => !entry.scope || satisfiesScope(viewer.grantedScopes, entry.scope)
    ).map((entry) => entry.title)
  );
  return items.filter((item) => titles.has(item.title));
}
