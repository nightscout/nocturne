import type { Page } from '@playwright/test';

export type Theme = 'light' | 'dark';
export type Viewport = 'desktop' | 'mobile';
export type Scenario = 'patient' | 'first-run';
export type Session = 'owner' | 'anonymous' | 'isolated';

/** The seeded tenant an entry's arrangement acts on. */
export interface ArrangeTenant {
	id: string;
	/** Origin the entry's route is resolved against, e.g. https://slug.nocturne.localhost:1612. */
	url: string;
	/** Bearer for the tenant's seeded owner. */
	accessToken: string;
}

export interface ArrangeRequest {
	/** GET when absent. */
	method?: string;
	/** Serialised as JSON. */
	body?: unknown;
}

export interface ArrangeContext {
	tenant: ArrangeTenant;
	apiUrl: string;
	/**
	 * Calls a tenant-scoped API path as the seeded owner. The API is reached at its own origin, so
	 * the tenant is carried by a forwarded host rather than the URL.
	 */
	fetch: <T>(path: string, request?: ArrangeRequest) => Promise<T>;
}

/** A screenshot to capture, declared in the manifest source. */
export interface ScreenshotDefinition {
	/** Kebab-case identifier; becomes the image filename stem and the id docs embed. */
	id: string;
	/**
	 * App route relative to the tenant origin. `{key}` holes are filled from what {@link
	 * ScreenshotDefinition.arrange} returned; a route that is exactly one hole may resolve to a full
	 * URL on another origin, which is how a share link is reached.
	 */
	route: string;
	/** Plain-language description of what the reader sees; rendered as the image alt text. */
	alt: string;
	scenario?: Scenario;
	/**
	 * Who is looking. `owner` (the default) and `anonymous` each share one browser context per
	 * theme with every entry of the same kind, so the sign-in is paid for once. `isolated` is a
	 * signed-out context created for this entry alone and closed after it — what a `prepare` that
	 * signs in needs, so the session it leaves behind cannot become the next anonymous entry's.
	 */
	session?: Session;
	viewport?: Viewport;
	/** Selector to crop the capture to; the viewport when absent. */
	clip?: string;
	/** Capture the whole document height instead of the viewport. Ignored when clip is set. */
	fullPage?: boolean;
	/** Named callout targets; bounding boxes are recorded into manifest.json at capture time. */
	anchors?: Record<string, string>;
	/**
	 * Server-side state the entry needs (a share link, a guest link, a clock face). Runs once per
	 * entry rather than once per theme: the arrangement is on the server, and both themes photograph
	 * the same one. The returned map fills the route's `{key}` holes; an arrangement that exists
	 * only for the state it leaves on the server returns an empty map.
	 */
	arrange?: (context: ArrangeContext) => Promise<Record<string, string>>;
	/** Runs after navigation, before capture (open a dialog, hover a chart). */
	prepare?: (page: Page) => Promise<void>;
}

export interface ManifestVariant {
	file: string;
	width: number;
	height: number;
}

export interface ManifestAnchor {
	x: number;
	y: number;
	width: number;
	height: number;
}

export interface ManifestEntry {
	alt: string;
	variants: Record<Theme, ManifestVariant>;
	anchors?: Record<string, ManifestAnchor>;
}

/** Shape of the generated manifest.json, keyed by screenshot id. */
export type Manifest = Record<string, ManifestEntry>;
