/**
 * Plausible tracking for the portal.
 *
 * The portal is served by GitHub Pages, which cannot reverse-proxy anything, so unlike the
 * marketing site — which serves the tracker first-party under /stats via the YARP gateway —
 * this has to name the analytics host absolutely. Ad blockers that match on hostname will
 * therefore stop some of these events; there is no way around that on Pages.
 *
 * Tracking is off unless VITE_PLAUSIBLE_DOMAIN is set at build time, so a self-hosted or
 * local build of the portal makes no request to anything.
 */
import { PLAUSIBLE_DOMAIN, PLAUSIBLE_HOST } from '$lib/config';

export const PLAUSIBLE_ENABLED = PLAUSIBLE_DOMAIN !== '';

// The manual variant sends no pageview of its own, so every URL Plausible sees is one
// `pageviewUrl` built.
export const PLAUSIBLE_SCRIPT_SRC = `${PLAUSIBLE_HOST}/js/script.manual.js`;
export const PLAUSIBLE_EVENT_API = `${PLAUSIBLE_HOST}/api/event`;

/** Sidebar sections, as reported by `Docs Nav`. `docs-nav.ts` is the source of these ids. */
export const DOCS_SECTION_IDS = [
  'getting-started',
  'installation',
  'authentication',
  'sharing',
  'food',
  'alerts',
  'bots',
  'configuration',
  'observability',
  'windows-widget',
  'connecting-apps',
  'sdks',
  'api-reference',
] as const;

/**
 * Off-site destinations worth counting, never a raw href. Links inside a /get-involved lane
 * card are excluded: they report `Get Involved Lane`, whose id already names the destination.
 */
export const OUTBOUND_DESTINATIONS = [
  'github',
  'github-labels',
  'discord',
  'demo',
] as const;

/**
 * The complete set of event names and, for each, every property key with the closed set of
 * values it may take. `track` drops anything absent from this table, so no page title, doc
 * heading, copied command, generated password or href can leave the browser as a property.
 *
 * Plausible attaches the current page URL to every custom event, so none of these needs a
 * `page` property — filter the dashboard by page instead.
 */
const ALLOWED_PROPS = {
  // --- Donations -------------------------------------------------------------------
  /** One-off giving: an outbound click to a donation destination. */
  'Donate Click': { destination: ['foundation'] },
  /** Recurring giving: a click through to one of the Stripe subscription tiers. */
  'Support Tier Click': { tier: ['supporter', 'sustainer', 'patron'] },
  /** A card on /get-involved. `donate` is an in-page jump, not an outbound click. */
  'Get Involved Lane': {
    lane: ['translate', 'support', 'donate', 'docs', 'peer', 'spread', 'sponsor', 'data'],
  },

  // --- Docs engagement -------------------------------------------------------------
  /** Someone took a command or config off a docs page — the page did its job. */
  'Docs Copy': { kind: ['code', 'password'] },
  /** Sidebar navigation, which a pageview alone cannot attribute. */
  'Docs Nav': { section: DOCS_SECTION_IDS },
  /** How far down a page the reader actually got. Fires once per milestone per pageview. */
  'Docs Scroll': { depth: ['25', '50', '75', '100'] },

  // --- Everything else -------------------------------------------------------------
  /** A click off the site. Not fired by anything that already reports its own event. */
  'Outbound Click': { destination: OUTBOUND_DESTINATIONS },
} as const satisfies Record<string, Record<string, readonly string[]>>;

export type AnalyticsEvent = keyof typeof ALLOWED_PROPS;

/**
 * The only query parameters a reported URL may keep. Plausible discards the rest at ingest,
 * but the URL still has to leave the browser first.
 */
const KEPT_QUERY_PARAMS = [
  'utm_source',
  'utm_medium',
  'utm_campaign',
  'utm_content',
  'utm_term',
  'ref',
];

declare global {
  interface Window {
    plausible?: (event: string, options?: { u?: string; props?: Record<string, string> }) => void;
  }
}

export function allowedProps(
  event: AnalyticsEvent,
  props: Record<string, string> = {},
): Record<string, string> {
  // `?? {}` rather than a bare lookup: this package's `check` typechecks neither .svelte nor
  // .ts, so a misspelled event name reaches here at runtime, and indexing `undefined` in the
  // filter below would throw out of whatever click handler called it. Unknown event, no props.
  const allowed: Record<string, readonly string[]> = ALLOWED_PROPS[event] ?? {};
  return Object.fromEntries(
    Object.entries(props).filter(
      // hasOwn, not `allowed[key]?.`: a property named `constructor` would otherwise
      // resolve to an Object.prototype member.
      ([key, value]) => Object.hasOwn(allowed, key) && allowed[key].includes(value),
    ),
  );
}

export function pageviewUrl(url: URL): string {
  const kept = new URLSearchParams();
  for (const name of KEPT_QUERY_PARAMS) {
    for (const value of url.searchParams.getAll(name)) kept.append(name, value);
  }
  const query = kept.toString();
  return `${url.origin}${url.pathname}${query ? `?${query}` : ''}`;
}

export function track(event: AnalyticsEvent, props?: Record<string, string>): void {
  window.plausible?.(event, {
    u: pageviewUrl(new URL(window.location.href)),
    props: allowedProps(event, props),
  });
}

export function trackPageview(url: URL): void {
  window.plausible?.('pageview', { u: pageviewUrl(url) });
}
