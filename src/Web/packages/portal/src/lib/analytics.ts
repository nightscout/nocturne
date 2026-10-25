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
import videos from '@nocturne/video/manifest.json';
import { PLAUSIBLE_DOMAIN, PLAUSIBLE_HOST } from '$lib/config';
import { DOCS_NAV_SECTIONS } from '$lib/data/docs-nav';

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
  'reports',
  'trackers',
  'alerts',
  'bots',
  'data-sources',
  'configuration',
  'observability',
  'windows-widget',
  'connecting-apps',
  'sdks',
  'api-reference',
] as const;

/** Docs videos, as reported by `Video Play` and `Video Complete`: the @nocturne/video manifest ids. */
export const VIDEO_IDS = Object.keys(videos);

/** The guides on /docs/installation, by the last segment of their path. */
export const INSTALL_PLATFORM_IDS = [
  'docker-compose',
  'portainer',
  'oracle-cloud',
  'byo-postgres',
  'reverse-proxy',
] as const;

/**
 * Values of `data-install-step` on an installation guide. A number is that guide's own step;
 * `manual` is Portainer's manual-deployment route, a proxy name is that proxy's section of the
 * reverse-proxy guide, and `next-steps` the guide's closing section, reached only by someone who
 * got to the end. A markdown guide sets them in its frontmatter (`rehype-install-steps.js`).
 */
export const INSTALL_STEP_IDS = [
  '1',
  '2',
  '3',
  '4',
  '5',
  'manual',
  'verify',
  'nginx',
  'traefik',
  'caddy',
  'next-steps',
] as const;

/** Engaged reading time, in seconds, reported by `Docs Read`. */
export const READ_MILESTONES = ['15', '30', '60', '120', '300', '600'] as const;

/**
 * Off-site destinations worth counting, never a raw href. Links inside a /get-involved lane
 * card are excluded: they report `Get Involved Lane`, whose id already names the destination.
 */
export const OUTBOUND_DESTINATIONS = [
  'github',
  'github-labels',
  'discord',
  'demo',
  'docs-issue',
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
  /** One-off giving: an outbound click to a donation destination, and which link it was. */
  'Donate Click': {
    destination: ['foundation'],
    placement: ['support-card', 'get-involved', 'docs-feedback'],
  },
  /** Recurring giving: a click through to one of the Stripe subscription tiers. */
  'Support Tier Click': { tier: ['supporter', 'sustainer', 'patron'] },
  /** A card on /get-involved. `donate` is an in-page jump, not an outbound click. */
  'Get Involved Lane': {
    lane: ['translate', 'support', 'donate', 'docs', 'peer', 'spread', 'sponsor', 'data'],
  },

  // --- Docs engagement -------------------------------------------------------------
  /**
   * Someone took a command or config off a docs page — the page did its job. On an
   * installation guide, `step` is the step the copied block sits under.
   */
  'Docs Copy': { kind: ['code', 'password'], step: INSTALL_STEP_IDS, doc_section: DOCS_SECTION_IDS },
  /** Sidebar navigation, which a pageview alone cannot attribute. `section` is the destination. */
  'Docs Nav': { section: DOCS_SECTION_IDS },
  /** How far down a page the reader actually got. Fires once per milestone per pageview. */
  'Docs Scroll': { depth: ['25', '50', '75', '100'], doc_section: DOCS_SECTION_IDS },
  /** The answer to "Was this helpful?" under a docs page. */
  'Docs Feedback': { helpful: ['yes', 'no'], doc_section: DOCS_SECTION_IDS },
  /** Time spent reading a page while it was visible and in use. Once per milestone per pageview. */
  'Docs Read': { seconds: READ_MILESTONES, doc_section: DOCS_SECTION_IDS },
  /** A docs video started. Once per video per pageview, so plays over completions is a rate. */
  'Video Play': { video: VIDEO_IDS },
  /** A docs video played to its last frame. Once per video per pageview. */
  'Video Complete': { video: VIDEO_IDS },

  // --- Installation ----------------------------------------------------------------
  /** A guide picked from the platform list on /docs/installation. */
  'Install Platform': { platform: INSTALL_PLATFORM_IDS },
  /** A step of an installation guide scrolled into view. Once per step per pageview. */
  'Install Step': { step: INSTALL_STEP_IDS },

  // --- Everything else -------------------------------------------------------------
  /** A click off the site. Not fired by anything that already reports its own event. */
  'Outbound Click': { destination: OUTBOUND_DESTINATIONS },
} as const satisfies Record<string, Record<string, readonly string[]>>;

export type AnalyticsEvent = keyof typeof ALLOWED_PROPS;

/** Properties of a pageview. Plausible keeps them apart from the custom events' properties. */
const PAGEVIEW_PROPS = { doc_section: DOCS_SECTION_IDS } as const;

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
  event: AnalyticsEvent | 'pageview',
  props: Record<string, string> = {},
): Record<string, string> {
  // `?? {}` rather than a bare lookup: this package's `check` typechecks neither .svelte nor
  // .ts, so a misspelled event name reaches here at runtime, and indexing `undefined` in the
  // filter below would throw out of whatever click handler called it. Unknown event, no props.
  const allowed: Record<string, readonly string[]> =
    event === 'pageview' ? PAGEVIEW_PROPS : (ALLOWED_PROPS[event] ?? {});
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

/**
 * The sidebar section a path belongs to: the section of the longest sidebar link that is the
 * path or one of its ancestors. A page outside the sidebar has none.
 */
export function docsSectionAt(pathname: string): string | undefined {
  let best: { section: string; length: number } | undefined;
  for (const section of DOCS_NAV_SECTIONS) {
    for (const { href } of section.items) {
      const within = pathname === href || pathname.startsWith(`${href}/`);
      // `/docs` is the root of every docs page, so it claims only itself.
      if (within && (href !== '/docs' || pathname === href) && href.length > (best?.length ?? -1)) {
        best = { section: section.id, length: href.length };
      }
    }
  }
  return best?.section;
}

/**
 * The step of an installation guide that an element sits under: the last `data-install-step`
 * marker before it in the document.
 */
export function installStepBefore(element: Element): string | undefined {
  let step: string | undefined;
  for (const marker of element.ownerDocument.querySelectorAll('[data-install-step]')) {
    if (!(marker.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING)) break;
    step = marker.getAttribute('data-install-step') ?? undefined;
  }
  return step;
}

/** Attached to every event so no caller has to; `allowedProps` keeps them only where declared. */
function pageProps(url: URL): Record<string, string> {
  const section = docsSectionAt(url.pathname);
  return section ? { doc_section: section } : {};
}

/**
 * An off-site link tagged as coming from these docs, for a destination that reads UTM: an
 * outbound link here sends no referrer. `placement` becomes `utm_content`.
 */
export function campaignUrl(href: string, placement: string): string {
  const url = new URL(href);
  url.searchParams.set('utm_source', 'nocturne-docs');
  url.searchParams.set('utm_medium', 'referral');
  url.searchParams.set('utm_content', placement);
  return url.toString();
}

export function track(event: AnalyticsEvent, props?: Record<string, string>): void {
  const url = new URL(window.location.href);
  window.plausible?.(event, {
    u: pageviewUrl(url),
    props: allowedProps(event, { ...pageProps(url), ...props }),
  });
}

export function trackPageview(url: URL): void {
  window.plausible?.('pageview', {
    u: pageviewUrl(url),
    props: allowedProps('pageview', pageProps(url)),
  });
}
