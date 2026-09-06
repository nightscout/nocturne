import { mkdir, readdir, rm, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import {
	chromium,
	errors,
	type Browser,
	type BrowserContext,
	type Page,
	type Request,
} from '@playwright/test';
import sharp from 'sharp';
import { definitions } from './manifest.js';
import { imagesDir, manifestPath } from './paths.js';
import { embeds, references, report } from './report.js';
import type {
	ArrangeContext,
	ArrangeRequest,
	Manifest,
	ManifestAnchor,
	ManifestVariant,
	Scenario,
	ScreenshotDefinition,
	Session,
	Theme,
	Viewport,
} from './types.js';

const API_URL = process.env.NOCTURNE_API_URL ?? 'http://localhost:1610';

// Pinned rather than inherited from the machine, so a re-capture differs only where the UI does.
const LOCALE = 'en-AU';
const TIMEZONE = 'Australia/Sydney';
const DEVICE_SCALE_FACTOR = 2;
const VIEWPORTS: Record<Viewport, { width: number; height: number }> = {
	desktop: { width: 1440, height: 900 },
	mobile: { width: 390, height: 844 },
};

/** mode-watcher reads this on hydration and toggles `dark` on the html element. */
const MODE_STORAGE_KEY = 'mode-watcher-mode';
/** @nocturne/coach's kill switch; without it every capture carries the tour's popovers and dots. */
const COACH_DISABLED_KEY = 'nocturne:coach-marks-disabled';

const SCENARIO_SEEDS: Record<Scenario, { sampleData: boolean; sampleDataDays: number }> = {
	patient: { sampleData: true, sampleDataDays: 7 },
	'first-run': { sampleData: false, sampleDataDays: 0 },
};

const WEBP_QUALITY = 85;
// Generous because a cold vite dev server transforms the whole module graph on the first
// navigation; settle() is what actually proves the page is ready.
const NAVIGATION_TIMEOUT_MS = 180_000;
const SETTLE_TIMEOUT_MS = 120_000;
// Rendering a tenant's chart data blocks the renderer for seconds at a time, so a probe that
// gets no answer means "still working", not "dead".
const PROBE_TIMEOUT_MS = 5_000;
const POLL_MS = 250;
const SCREENSHOT_TIMEOUT_MS = 30_000;
// What a prepare's clicks and waits get. Deliberately not the settle budget: settle waits on data
// arriving, whereas a control a prepare names either shows up promptly or is not there at all.
const ACTION_TIMEOUT_MS = 30_000;
const SELECTOR_TIMEOUT_MS = 10_000;
const SETTLE_MS = 750;

/** SvelteKit serves every remote query and command under this prefix. */
const REMOTE_ENDPOINT = '/_app/remote/';

const ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const PLACEHOLDER_PATTERN = /\{([A-Za-z][A-Za-z0-9]*)\}/g;
/** A route that is nothing but one hole, which is the only shape allowed to resolve off-origin. */
const WHOLE_ROUTE_PLACEHOLDER = /^\{[A-Za-z][A-Za-z0-9]*\}$/;

interface Box {
	x: number;
	y: number;
	width: number;
	height: number;
}

interface DevTenant {
	id: string;
	url: string;
	loginLink: string;
	accessToken: string;
}

const remoteCallsInFlight = new WeakMap<Page, Set<Request>>();

function validate(candidates: ScreenshotDefinition[]): string[] {
	const problems: string[] = [];
	const seen = new Set<string>();

	for (const [index, definition] of candidates.entries()) {
		const where = definition.id ? `"${definition.id}"` : `definition #${index}`;

		if (!ID_PATTERN.test(definition.id)) {
			problems.push(`${where}: id must be kebab-case (lowercase letters, digits and single hyphens)`);
		} else if (seen.has(definition.id)) {
			problems.push(`${where}: duplicate id`);
		} else {
			seen.add(definition.id);
		}

		const route = definition.route ?? '';
		if (!route.startsWith('/') && !WHOLE_ROUTE_PLACEHOLDER.test(route)) {
			problems.push(`${where}: route must be a path beginning with "/", or a single placeholder`);
		}
		if (route.match(PLACEHOLDER_PATTERN) && !definition.arrange) {
			problems.push(`${where}: route has placeholders but no arrange to fill them`);
		}
		if (!definition.alt?.trim()) {
			problems.push(`${where}: alt is required`);
		}
		for (const [name, selector] of Object.entries(definition.anchors ?? {})) {
			if (!selector.trim()) problems.push(`${where}: anchor "${name}" has an empty selector`);
		}
	}

	return problems;
}

async function seedTenant(scenario: Scenario, runStart: Date): Promise<DevTenant> {
	const seed = SCENARIO_SEEDS[scenario];
	const response = await fetch(`${API_URL}/api/v4/dev-only/admin/seed-tenant`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({
			slug: `docs-${runStart.getTime()}-${scenario}`,
			displayName: 'Nocturne documentation',
			ownerUsername: 'dev',
			...seed,
		}),
	});

	if (!response.ok) {
		throw new Error(
			`seed-tenant failed for scenario "${scenario}": ${response.status} ${await response.text()}`,
		);
	}

	const body = (await response.json()) as Partial<DevTenant> & { tenantId?: string };
	if (!body.tenantId || !body.url || !body.loginLink || !body.accessToken) {
		throw new Error(
			`seed-tenant returned no tenantId/url/loginLink/accessToken for scenario "${scenario}"`,
		);
	}
	return {
		id: body.tenantId,
		url: body.url,
		loginLink: body.loginLink,
		accessToken: body.accessToken,
	};
}

/**
 * Tenant resolution reads the forwarded host rather than the URL's own, so an entry's arrangement
 * can call the API directly on its unencrypted origin and still land on the seeded tenant — which
 * the gateway's local certificate would otherwise make awkward from Node.
 */
function arrangeContext(definition: ScreenshotDefinition, tenant: DevTenant): ArrangeContext {
	const forwardedHost = new URL(tenant.url).host;

	return {
		tenant: { id: tenant.id, url: tenant.url, accessToken: tenant.accessToken },
		apiUrl: API_URL,
		fetch: async <T>(path: string, request: ArrangeRequest = {}): Promise<T> => {
			const hasBody = request.body !== undefined;
			const response = await fetch(`${API_URL}${path}`, {
				method: request.method ?? 'GET',
				headers: {
					Authorization: `Bearer ${tenant.accessToken}`,
					'X-Forwarded-Host': forwardedHost,
					...(hasBody ? { 'Content-Type': 'application/json' } : {}),
				},
				...(hasBody ? { body: JSON.stringify(request.body) } : {}),
			});

			if (!response.ok) {
				throw new Error(
					`${definition.id}: ${request.method ?? 'GET'} ${path} returned ${response.status} ${await response.text()}`,
				);
			}
			return (await response.json()) as T;
		},
	};
}

/** Fails rather than navigating to a URL still carrying a hole, which would 404 somewhere obscure. */
function resolveRoute(definition: ScreenshotDefinition, values: Record<string, string>): string {
	return definition.route.replace(PLACEHOLDER_PATTERN, (_, key: string) => {
		const value = values[key];
		if (value === undefined) {
			throw new Error(`${definition.id}: route placeholder {${key}} was not returned by arrange`);
		}
		return value;
	});
}

/**
 * A page of its own per entry. The session's cookies live on the context, so this costs nothing but
 * the navigation each entry performs anyway — and it stops one route's leftovers (timers, listeners,
 * a renderer the last route left busy) from deciding whether the next route can be photographed.
 */
async function openPage(context: BrowserContext): Promise<Page> {
	const page = await context.newPage();
	const inFlight = new Set<Request>();
	remoteCallsInFlight.set(page, inFlight);
	page.on('request', (request) => {
		if (request.url().includes(REMOTE_ENDPOINT)) inFlight.add(request);
	});
	page.on('requestfinished', (request) => inFlight.delete(request));
	page.on('requestfailed', (request) => inFlight.delete(request));
	return page;
}

async function openSession(
	browser: Browser,
	tenant: DevTenant,
	session: Session,
	theme: Theme,
	viewport: Viewport,
	runStart: Date,
): Promise<BrowserContext> {
	const context = await browser.newContext({
		ignoreHTTPSErrors: true,
		locale: LOCALE,
		timezoneId: TIMEZONE,
		colorScheme: theme,
		reducedMotion: 'reduce',
		viewport: VIEWPORTS[viewport],
		deviceScaleFactor: DEVICE_SCALE_FACTOR,
	});

	// An entry's prepare navigates and clicks on this context too, so the budgets live here rather
	// than at each call site.
	context.setDefaultNavigationTimeout(NAVIGATION_TIMEOUT_MS);
	context.setDefaultTimeout(ACTION_TIMEOUT_MS);

	await context.addInitScript(
		(entries: [string, string][]) => {
			for (const [key, value] of entries) window.localStorage.setItem(key, value);
		},
		[
			[MODE_STORAGE_KEY, theme],
			[COACH_DISABLED_KEY, 'true'],
		] as [string, string][],
	);
	// Relative-time labels ("3 minutes ago") would otherwise differ between the first and last
	// capture of a run. Fixing the clock leaves timers running, so the app still hydrates.
	await context.clock.setFixedTime(runStart);

	// Signing in once puts the session cookies on the context, where every later page inherits
	// them. `anonymous` and `isolated` both start signed out; they differ only in whether the
	// context is shared with the entries around them.
	if (session === 'owner') {
		const page = await openPage(context);
		await page.goto(tenant.loginLink, { waitUntil: 'domcontentloaded' });
		await page.close();
	}
	return context;
}

/**
 * Runs in the page. Every entry names something that would be photographed mid-flight; an empty
 * result is the only proof the route finished loading. The theme check doubles as proof that
 * hydration finished and the server-rendered markup has been replaced by the live one.
 */
function unsettled(dark: boolean): string[] {
	const reasons: string[] = [];
	if (document.documentElement.classList.contains('dark') !== dark) reasons.push('theme not applied');
	if (document.fonts.status !== 'loaded') reasons.push('fonts still loading');
	// A site that has never received a reading holds the glucose indicators' placeholder for good,
	// so inside one it is a steady state rather than a load in progress.
	const skeletons = document.querySelectorAll('.animate-pulse:not(.glucose-value-indicator *)').length;
	if (skeletons > 0) reasons.push(`${skeletons} skeleton placeholders`);
	const spinners = document.querySelectorAll('.animate-spin').length;
	if (spinners > 0) reasons.push(`${spinners} spinners`);
	if (/\bLoading\b/.test(document.body.innerText)) reasons.push('"Loading" still on screen');
	if (/\bFailed to load\b/.test(document.body.innerText)) reasons.push('a load error on screen');
	// The kill switch leaves ineligible marks in the DOM as display:none, so only a visible one
	// means it broke.
	const marks = document.querySelectorAll('.coach-popover, .coach-hotspot');
	if ([...marks].some((mark) => (mark as HTMLElement).checkVisibility())) {
		reasons.push('coach mark on screen');
	}
	return reasons;
}

/** Null when the renderer did not answer in time, which is a state of its own rather than "clean". */
async function probe(page: Page, theme: Theme): Promise<string[] | null> {
	return Promise.race([
		page.evaluate(unsettled, theme === 'dark').catch(() => null),
		page.waitForTimeout(PROBE_TIMEOUT_MS).then(() => null),
	]);
}

/**
 * Holds until the route has come up clean on enough consecutive polls to cover SETTLE_MS. Anything
 * that arrives late — a widget's skeleton, a coach mark — restarts that window rather than landing
 * in the frame after the last check passed.
 */
async function settle(page: Page, definition: ScreenshotDefinition, theme: Theme): Promise<void> {
	const inFlight = remoteCallsInFlight.get(page);
	const deadline = Date.now() + SETTLE_TIMEOUT_MS;
	let quietPolls = 0;
	let unanswered = 0;

	for (;;) {
		const observed = await probe(page, theme);
		if (observed === null) unanswered++;
		const quiet = observed?.length === 0 && !inFlight?.size;
		quietPolls = quiet ? quietPolls + 1 : 0;
		// The window a run of polls covers is the gaps between them, one fewer than the polls.
		if ((quietPolls - 1) * POLL_MS >= SETTLE_MS) return;

		if (Date.now() >= deadline) {
			const reasons = (await probe(page, theme)) ?? [];
			if (inFlight?.size) reasons.push(`${inFlight.size} remote queries in flight`);
			// Separates a route still waiting on data from one whose main thread is pegged, which
			// is also what stalls the screenshot itself.
			if (unanswered > 0) reasons.push(`renderer did not answer ${unanswered} probes`);
			throw new Error(
				`${definition.id}: ${definition.route} did not settle within ${SETTLE_TIMEOUT_MS}ms (${reasons.join('; ')})`,
			);
		}
		await page.waitForTimeout(POLL_MS);
	}
}

/**
 * Runs in the page. Reads every box the capture needs against one layout, in document coordinates:
 * resolved across separate calls, a scroll between two of them would put the capture and the
 * callouts that mark it up in different coordinate frames. Written without a helper function
 * because esbuild's name-preserving wrapper does not survive serialisation into the page.
 */
function measureBoxes(selectors: string[]) {
	return {
		scroll: { x: window.scrollX, y: window.scrollY },
		boxes: selectors.map((selector) => {
			const element = document.querySelector(selector);
			if (!element) return null;
			const rect = element.getBoundingClientRect();
			if (rect.width === 0 || rect.height === 0) return null;
			return {
				x: rect.left + window.scrollX,
				y: rect.top + window.scrollY,
				width: rect.width,
				height: rect.height,
			};
		}),
	};
}

interface Layout {
	scroll: { x: number; y: number };
	clip: Box | null;
	anchors: [string, Box][];
}

async function measure(page: Page, definition: ScreenshotDefinition): Promise<Layout> {
	const targets: { label: string; selector: string }[] = [];
	if (definition.clip) targets.push({ label: 'clip', selector: definition.clip });
	for (const [name, selector] of Object.entries(definition.anchors ?? {})) {
		targets.push({ label: `anchor "${name}"`, selector });
	}

	const deadline = Date.now() + SELECTOR_TIMEOUT_MS;
	for (;;) {
		const measured = await page.evaluate(
			measureBoxes,
			targets.map((target) => target.selector),
		);
		const missing = targets
			.filter((_, index) => measured.boxes[index] === null)
			.map((target) => `${target.label} (${target.selector})`);

		if (missing.length === 0) {
			const boxes = measured.boxes as Box[];
			const clip = definition.clip ? boxes[0] : null;
			const offset = definition.clip ? 1 : 0;
			return {
				scroll: measured.scroll,
				clip,
				anchors: Object.keys(definition.anchors ?? {}).map((name, index) => [
					name,
					boxes[index + offset],
				]),
			};
		}
		if (Date.now() >= deadline) {
			// See the package README on why this fails the run rather than warning.
			throw new Error(`${definition.id}: matched no visible element for ${missing.join('; ')}`);
		}
		await page.waitForTimeout(POLL_MS);
	}
}

function toImagePixels(box: Box, origin: { x: number; y: number }): ManifestAnchor {
	return {
		x: Math.round((box.x - origin.x) * DEVICE_SCALE_FACTOR),
		y: Math.round((box.y - origin.y) * DEVICE_SCALE_FACTOR),
		width: Math.round(box.width * DEVICE_SCALE_FACTOR),
		height: Math.round(box.height * DEVICE_SCALE_FACTOR),
	};
}

/**
 * Playwright drives a screenshot through the renderer (fonts, animation freezing, a frame), so a
 * blocked main thread stalls it with a timeout that says nothing about why.
 */
async function expose(
	definition: ScreenshotDefinition,
	take: () => Promise<Buffer>,
): Promise<Buffer> {
	try {
		return await take();
	} catch (error) {
		if (error instanceof errors.TimeoutError) {
			throw new Error(
				`${definition.id}: screenshot timed out after ${SCREENSHOT_TIMEOUT_MS}ms; the renderer stopped producing frames`,
			);
		}
		throw error;
	}
}

/**
 * A clipped shot is a full-page capture narrowed to the measured box rather than
 * `locator.screenshot()`, which scrolls the element into view itself and so would render against a
 * layout the boxes were not measured in.
 */
async function shoot(
	page: Page,
	definition: ScreenshotDefinition,
	layout: Layout,
): Promise<{ png: Buffer; origin: { x: number; y: number } }> {
	const common = { animations: 'disabled', timeout: SCREENSHOT_TIMEOUT_MS } as const;

	const clip = layout.clip;
	if (clip) {
		return {
			png: await expose(definition, () => page.screenshot({ ...common, fullPage: true, clip })),
			origin: clip,
		};
	}
	if (definition.fullPage) {
		return {
			png: await expose(definition, () => page.screenshot({ ...common, fullPage: true })),
			origin: { x: 0, y: 0 },
		};
	}
	return {
		png: await expose(definition, () => page.screenshot(common)),
		origin: layout.scroll,
	};
}

/**
 * A Playwright locator error names the selector that timed out and nothing else, so on its own it
 * says which control moved but not which screenshot was reaching for it.
 */
async function prepare(page: Page, definition: ScreenshotDefinition): Promise<void> {
	try {
		await definition.prepare!(page);
	} catch (error) {
		const detail = error instanceof Error ? error.message : String(error);
		throw new Error(`${definition.id}: prepare failed: ${detail}`, { cause: error });
	}
}

async function encode(png: Buffer, file: string): Promise<ManifestVariant> {
	const { data, info } = await sharp(png)
		.webp({ quality: WEBP_QUALITY })
		.toBuffer({ resolveWithObject: true });
	await writeFile(join(imagesDir, file), data);
	// Package-root-relative.
	return { file: `images/${file}`, width: info.width, height: info.height };
}

interface Capture {
	variant: ManifestVariant;
	anchors?: Record<string, ManifestAnchor>;
}

async function captureTheme(
	page: Page,
	definition: ScreenshotDefinition,
	theme: Theme,
	target: string,
): Promise<Capture> {
	await page.goto(target, { waitUntil: 'domcontentloaded' });
	await settle(page, definition, theme);
	if (definition.prepare) {
		await prepare(page, definition);
		await settle(page, definition, theme);
	}

	const layout = await measure(page, definition);
	const { png, origin } = await shoot(page, definition, layout);
	const variant = await encode(png, `${definition.id}.${theme}.webp`);

	if (layout.anchors.length === 0) return { variant };

	const anchors: Record<string, ManifestAnchor> = {};
	for (const [name, box] of layout.anchors) {
		const anchor = toImagePixels(box, origin);
		if (
			anchor.x < 0 ||
			anchor.y < 0 ||
			anchor.x + anchor.width > variant.width ||
			anchor.y + anchor.height > variant.height
		) {
			throw new Error(
				`${definition.id}: anchor "${name}" lies outside the captured image; capture with fullPage or clip to a region containing it`,
			);
		}
		anchors[name] = anchor;
	}
	return { variant, anchors };
}

function describe(box: ManifestAnchor | undefined): string {
	return box ? `${box.width}x${box.height} at ${box.x},${box.y}` : 'nothing';
}

/**
 * The docs draw light's anchor boxes over whichever variant the reader's theme shows, so the two
 * have to agree on both the frame and every box in it. They come out of identical layouts, so a
 * difference is a theme-dependent layout shift the callouts cannot survive.
 */
function assertSharedFrame(id: string, light: Capture, dark: Capture): void {
	if (light.variant.width !== dark.variant.width || light.variant.height !== dark.variant.height) {
		throw new Error(
			`${id}: light (${light.variant.width}x${light.variant.height}) and dark (${dark.variant.width}x${dark.variant.height}) differ in size, so the anchor boxes cannot describe both`,
		);
	}

	for (const [name, box] of Object.entries(light.anchors ?? {})) {
		const counterpart = dark.anchors?.[name];
		if (
			counterpart &&
			counterpart.x === box.x &&
			counterpart.y === box.y &&
			counterpart.width === box.width &&
			counterpart.height === box.height
		) {
			continue;
		}
		throw new Error(
			`${id}: anchor "${name}" is ${describe(box)} in light and ${describe(counterpart)} in dark`,
		);
	}
}

/** Images left behind by an id that has since been renamed or dropped. */
async function prune(manifest: Manifest): Promise<string[]> {
	const captured = new Set(
		Object.values(manifest).flatMap((entry) =>
			Object.values(entry.variants).map((variant) => basename(variant.file)),
		),
	);

	const removed: string[] = [];
	for (const file of await readdir(imagesDir)) {
		if (file.startsWith('.') || captured.has(file)) continue;
		await rm(join(imagesDir, file));
		removed.push(file);
	}
	return removed;
}

function serialize(manifest: Manifest): string {
	const sorted = Object.fromEntries(
		Object.entries(manifest).sort(([left], [right]) => left.localeCompare(right)),
	);
	return `${JSON.stringify(sorted, null, '\t')}\n`;
}

async function main(): Promise<void> {
	const problems = validate(definitions);
	if (problems.length > 0) {
		console.error(`Invalid screenshot definitions:\n  ${problems.join('\n  ')}`);
		process.exit(1);
	}
	if (process.argv.includes('--validate')) {
		console.log(`${definitions.length} screenshot definitions are valid.`);
		return;
	}

	const runStart = new Date();
	await mkdir(imagesDir, { recursive: true });

	const tenants = new Map<Scenario, DevTenant>();
	const sessions = new Map<string, BrowserContext>();
	const manifest: Manifest = {};
	const browser = await chromium.launch();

	const tenantFor = async (scenario: Scenario): Promise<DevTenant> => {
		const existing = tenants.get(scenario);
		if (existing) return existing;
		const seeded = await seedTenant(scenario, runStart);
		tenants.set(scenario, seeded);
		return seeded;
	};

	try {
		for (const definition of definitions) {
			const scenario = definition.scenario ?? 'patient';
			const session = definition.session ?? 'owner';
			const viewport = definition.viewport ?? 'desktop';
			const tenant = await tenantFor(scenario);

			const sessionFor = async (theme: Theme): Promise<BrowserContext> => {
				const key = `${scenario}|${session}|${theme}|${viewport}`;
				const existing = sessions.get(key);
				if (existing) return existing;
				const context = await openSession(browser, tenant, session, theme, viewport, runStart);
				sessions.set(key, context);
				return context;
			};

			const values = definition.arrange
				? await definition.arrange(arrangeContext(definition, tenant))
				: {};
			const target = new URL(resolveRoute(definition, values), tenant.url).href;

			const photograph = async (theme: Theme): Promise<Capture> => {
				const context =
					session === 'isolated'
						? await openSession(browser, tenant, session, theme, viewport, runStart)
						: await sessionFor(theme);
				const page = await openPage(context);
				try {
					return await captureTheme(page, definition, theme, target);
				} finally {
					try {
						await page.close();
					} finally {
						if (session === 'isolated') await context.close();
					}
				}
			};

			// Both themes resolve the anchors, so a selector that has gone stale fails the run
			// whichever theme it broke under.
			const light = await photograph('light');
			const dark = await photograph('dark');
			if (light.anchors) assertSharedFrame(definition.id, light, dark);

			manifest[definition.id] = {
				alt: definition.alt,
				variants: { light: light.variant, dark: dark.variant },
				...(light.anchors ? { anchors: light.anchors } : {}),
			};
			console.log(`captured ${definition.id}`);
		}
	} finally {
		await browser.close();
	}

	await writeFile(manifestPath, serialize(manifest));

	await report(references, embeds);

	// Only once nothing above has failed: a run that stopped early leaves its images and its
	// tenants behind to be inspected.
	const pruned = await prune(manifest);
	if (pruned.length > 0) console.log(`pruned unreferenced images: ${pruned.join(', ')}`);

	for (const tenant of tenants.values()) {
		const response = await fetch(`${API_URL}/api/v4/dev-only/admin/tenants/${tenant.id}`, {
			method: 'DELETE',
		}).catch(() => null);
		if (!response?.ok) console.warn(`could not remove seeded tenant ${tenant.id}`);
	}
}

await main();
