import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { chromium, type Browser, type Page, type Request } from 'playwright';
import sharp from 'sharp';
import { definitions } from './manifest.js';
import { imagesDir, manifestPath } from './paths.js';
import { findBrokenReferences } from './references.js';
import type {
	Manifest,
	ManifestAnchor,
	ManifestVariant,
	Scenario,
	ScreenshotDefinition,
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
/** @nocturne/coach's kill switch; the first-run tour would otherwise spotlight every capture. */
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
const SELECTOR_TIMEOUT_MS = 10_000;
const SETTLE_MS = 750;

/** SvelteKit serves every remote query and command under this prefix. */
const REMOTE_ENDPOINT = '/_app/remote/';

const ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

interface Origin {
	x: number;
	y: number;
}

interface DevTenant {
	url: string;
	loginLink: string;
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

		if (!definition.route?.startsWith('/')) {
			problems.push(`${where}: route must be a path beginning with "/"`);
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

	const body = (await response.json()) as Partial<DevTenant>;
	if (!body.url || !body.loginLink) {
		throw new Error(`seed-tenant returned no url/loginLink for scenario "${scenario}"`);
	}
	return { url: body.url, loginLink: body.loginLink };
}

async function openSession(
	browser: Browser,
	tenant: DevTenant,
	theme: Theme,
	viewport: Viewport,
	runStart: Date,
): Promise<Page> {
	const context = await browser.newContext({
		ignoreHTTPSErrors: true,
		locale: LOCALE,
		timezoneId: TIMEZONE,
		colorScheme: theme,
		reducedMotion: 'reduce',
		viewport: VIEWPORTS[viewport],
		deviceScaleFactor: DEVICE_SCALE_FACTOR,
	});

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

	const page = await context.newPage();
	const inFlight = new Set<Request>();
	remoteCallsInFlight.set(page, inFlight);
	page.on('request', (request) => {
		if (request.url().includes(REMOTE_ENDPOINT)) inFlight.add(request);
	});
	page.on('requestfinished', (request) => inFlight.delete(request));
	page.on('requestfailed', (request) => inFlight.delete(request));

	await page.goto(tenant.loginLink, {
		waitUntil: 'domcontentloaded',
		timeout: NAVIGATION_TIMEOUT_MS,
	});
	return page;
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
	if (/\bLoading\b/.test(document.body.innerText)) reasons.push('"Loading" still on screen');
	// The coach kill switch is set before any page script runs, so a popover here means it broke.
	if (document.querySelector('.coach-popover')) reasons.push('coach mark on screen');
	return reasons;
}

/**
 * Holds until the route has come up clean on enough consecutive polls to cover SETTLE_MS. Anything
 * that arrives late — a widget's skeleton, a coach mark — restarts that window rather than landing
 * in the frame after the last check passed.
 */
async function settle(page: Page, definition: ScreenshotDefinition, theme: Theme): Promise<void> {
	const inFlight = remoteCallsInFlight.get(page);
	const deadline = Date.now() + SETTLE_TIMEOUT_MS;
	let reasons: string[] = [];
	let quietPolls = 0;
	let unanswered = 0;

	for (;;) {
		const observed = await Promise.race([
			page.evaluate(unsettled, theme === 'dark').catch(() => null),
			page.waitForTimeout(PROBE_TIMEOUT_MS).then(() => null),
		]);
		if (observed) {
			reasons = [...observed];
			if (inFlight?.size) reasons.push(`${inFlight.size} remote queries in flight`);
		} else {
			unanswered++;
		}
		quietPolls = observed && reasons.length === 0 ? quietPolls + 1 : 0;
		if (quietPolls * POLL_MS >= SETTLE_MS) return;

		// A skeleton captured silently is worse than no capture at all.
		if (Date.now() >= deadline) {
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

function toImagePixels(
	box: { x: number; y: number; width: number; height: number },
	origin: Origin,
): ManifestAnchor {
	return {
		x: Math.round((box.x - origin.x) * DEVICE_SCALE_FACTOR),
		y: Math.round((box.y - origin.y) * DEVICE_SCALE_FACTOR),
		width: Math.round(box.width * DEVICE_SCALE_FACTOR),
		height: Math.round(box.height * DEVICE_SCALE_FACTOR),
	};
}

async function resolveAnchors(
	page: Page,
	definition: ScreenshotDefinition,
	origin: Origin,
): Promise<Record<string, ManifestAnchor> | undefined> {
	const declared = Object.entries(definition.anchors ?? {});
	if (declared.length === 0) return undefined;

	const anchors: Record<string, ManifestAnchor> = {};
	for (const [name, selector] of declared) {
		const box = await page
			.locator(selector)
			.first()
			.boundingBox({ timeout: SELECTOR_TIMEOUT_MS })
			.catch(() => null);
		// A callout pointing at markup that no longer exists is the whole reason this pipeline
		// runs in CI. Never downgrade it to a warning.
		if (!box) {
			throw new Error(
				`${definition.id}: anchor "${name}" matched no visible element for selector ${selector}`,
			);
		}
		anchors[name] = toImagePixels(box, origin);
	}
	return anchors;
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
		if (error instanceof Error && error.message.includes('Timeout')) {
			throw new Error(
				`${definition.id}: screenshot timed out after ${SCREENSHOT_TIMEOUT_MS}ms; the renderer stopped producing frames`,
			);
		}
		throw error;
	}
}

async function shoot(
	page: Page,
	definition: ScreenshotDefinition,
): Promise<{ png: Buffer; origin: Origin }> {
	if (!definition.clip) {
		return {
			png: await expose(definition, () =>
				page.screenshot({
					animations: 'disabled',
					timeout: SCREENSHOT_TIMEOUT_MS,
					fullPage: definition.fullPage ?? false,
				}),
			),
			origin: { x: 0, y: 0 },
		};
	}

	const locator = page.locator(definition.clip).first();
	await locator.scrollIntoViewIfNeeded({ timeout: SELECTOR_TIMEOUT_MS }).catch(() => undefined);
	const box = await locator.boundingBox({ timeout: SELECTOR_TIMEOUT_MS }).catch(() => null);
	if (!box) {
		throw new Error(
			`${definition.id}: clip matched no visible element for selector ${definition.clip}`,
		);
	}
	return {
		png: await expose(definition, () =>
			locator.screenshot({ animations: 'disabled', timeout: SCREENSHOT_TIMEOUT_MS }),
		),
		origin: box,
	};
}

async function encode(png: Buffer, file: string): Promise<ManifestVariant> {
	const { data, info } = await sharp(png)
		.webp({ quality: WEBP_QUALITY })
		.toBuffer({ resolveWithObject: true });
	await writeFile(join(imagesDir, file), data);
	// Package-root-relative, matching how consumers resolve it via the "./images/*" export.
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
	tenantUrl: string,
): Promise<Capture> {
	await page.goto(new URL(definition.route, tenantUrl).href, {
		waitUntil: 'domcontentloaded',
		timeout: NAVIGATION_TIMEOUT_MS,
	});
	await settle(page, definition, theme);
	if (definition.prepare) {
		await definition.prepare(page);
		await settle(page, definition, theme);
	}

	const { png, origin } = await shoot(page, definition);
	const variant = await encode(png, `${definition.id}.${theme}.webp`);
	const anchors = await resolveAnchors(page, definition, origin);
	for (const [name, box] of Object.entries(anchors ?? {})) {
		if (box.x < 0 || box.y < 0 || box.x + box.width > variant.width || box.y + box.height > variant.height) {
			throw new Error(
				`${definition.id}: anchor "${name}" lies outside the captured image; capture with fullPage or clip to a region containing it`,
			);
		}
	}
	return anchors ? { variant, anchors } : { variant };
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
	const sessions = new Map<string, Page>();
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
			const viewport = definition.viewport ?? 'desktop';
			const tenant = await tenantFor(scenario);

			const sessionFor = async (theme: Theme): Promise<Page> => {
				const key = `${scenario}|${theme}|${viewport}`;
				const existing = sessions.get(key);
				if (existing) return existing;
				const page = await openSession(browser, tenant, theme, viewport, runStart);
				sessions.set(key, page);
				return page;
			};

			// Both themes resolve the anchors, so a selector that has gone stale fails the run
			// whichever theme it broke under; the boxes themselves are theme-independent.
			const light = await captureTheme(await sessionFor('light'), definition, 'light', tenant.url);
			const dark = await captureTheme(await sessionFor('dark'), definition, 'dark', tenant.url);

			manifest[definition.id] = {
				alt: definition.alt,
				variants: { light: light.variant, dark: dark.variant },
				...(light.anchors ? { anchors: light.anchors } : {}),
				capturedAt: runStart.toISOString(),
			};
			console.log(`captured ${definition.id}`);
		}
	} finally {
		await browser.close();
	}

	await writeFile(manifestPath, serialize(manifest));

	const broken = await findBrokenReferences();
	if (broken.length > 0) {
		console.error(`Broken screenshot references:\n  ${broken.join('\n  ')}`);
		process.exit(1);
	}
}

await main();
