import { createHmac } from 'node:crypto';
import type { Locator, Page } from '@playwright/test';
import { stringField } from './json.js';
import type { ArrangeContext, ScreenshotDefinition } from './types.js';

/** Rich enough that the public view is worth a screenshot; still short of everything on offer. */
const SHARED_CATEGORIES = ['glucose.read', 'treatments.read', 'devices.read'];

/** Turns the tenant's public link on, widens what it shows, and hands back the link to navigate to. */
async function openPublicShare({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	const rotated = await fetch('/api/v4/share/rotate', { method: 'POST' });
	await fetch('/api/v4/share/scopes', { method: 'PUT', body: { scopes: SHARED_CATEGORIES } });
	await fetch('/api/v4/share/full-history', { method: 'PUT', body: { fullHistory: true } });

	const shareUrl = stringField(rotated, 'url');
	if (!shareUrl) throw new Error('rotating the share link returned no URL');
	return { shareUrl };
}

/**
 * The redacted address is what the card rests on, so a share arranged through the API is already in
 * the state worth photographing, and the image no longer changes every capture the way a minted
 * address did.
 */
async function settledPublicLink(page: Page): Promise<void> {
	await page.getByTestId('public-access-url-redacted').waitFor();
}

async function inviteAGuest({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	await fetch('/api/v4/guest-links', { method: 'POST', body: { label: 'School nurse' } });
	return {};
}

/**
 * Clicks `trigger` until `opened` appears. The settle check cannot tell a server-rendered page from a
 * hydrated one, and a click that lands before hydration is dropped without a trace. Only for a
 * trigger that opens and never closes: once `opened` shows, or `trigger` is gone, it stops clicking.
 */
async function openWith(trigger: Locator, opened: Locator): Promise<void> {
	for (let attempt = 0; attempt < 10; attempt++) {
		if (await opened.isVisible()) return;
		if (await trigger.isVisible()) await trigger.click();
		try {
			await opened.waitFor({ timeout: 3000 });
			return;
		} catch {
			// Not hydrated yet; click again.
		}
	}
	await opened.waitFor();
}

/** The name {@link sensorTrackerWithThresholds} saves its definition under, for the prepares to find it. */
const THRESHOLD_TRACKER_NAME = '10-day sensor';

/**
 * The seeded trackers carry no notification thresholds, so the editor would open on an empty ladder.
 * This one is a new definition rather than thresholds added to a seeded one: with no instance running
 * it can never fire, so no alert raised by the arrangement reaches a later capture.
 */
async function sensorTrackerWithThresholds({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	// Two entries photograph this editor in one tenant; a second copy would make its Edit button ambiguous.
	const existing = await fetch('/api/v4/trackers/definitions');
	if (Array.isArray(existing) && existing.some((d) => stringField(d, 'name') === THRESHOLD_TRACKER_NAME)) return {};

	await fetch('/api/v4/trackers/definitions', {
		method: 'POST',
		body: {
			name: THRESHOLD_TRACKER_NAME,
			category: 'Sensor',
			mode: 'Duration',
			lifespanHours: 240,
			triggerEventTypes: ['Sensor Start'],
			dashboardVisibility: 'Always',
			visibility: 'Private',
			notificationThresholds: [
				{ urgency: 'Info', hours: -24, description: 'Sensor ends tomorrow', displayOrder: 0 },
				{ urgency: 'Warn', hours: -2, description: 'Change the sensor soon', displayOrder: 1 },
				{ urgency: 'Urgent', hours: 240, description: 'Sensor has expired', displayOrder: 2 },
			],
		},
	});
	return {};
}

async function openThresholdTrackerEditor(page: Page): Promise<void> {
	const edit = page.getByRole('button', { name: `Edit ${THRESHOLD_TRACKER_NAME}` });
	await openWith(page.getByRole('tab', { name: /Definitions/ }), edit);
	await openWith(edit, page.getByTestId('tracker-editor'));
}

async function seededClockFace({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	const faces = await fetch('/api/v4/clockfaces');
	const clockId = stringField(Array.isArray(faces) ? faces[0] : undefined, 'id');
	if (!clockId) throw new Error('the seeded tenant has no clock face');
	return { clockId };
}

const BASE32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
const TOTP_PERIOD_SECONDS = 30;
const TOTP_DIGITS = 6;

function decodeBase32(secret: string): Buffer {
	let bits = '';
	for (const character of secret.toUpperCase()) {
		const value = BASE32_ALPHABET.indexOf(character);
		// Skipping the character instead would decode the enrolment secret to a different key and
		// fail as a wrong six digits, which says nothing about where the corruption came from.
		if (value < 0) throw new Error(`"${character}" is not a base32 character`);
		bits += value.toString(2).padStart(5, '0');
	}

	const bytes: number[] = [];
	for (let offset = 0; offset + 8 <= bits.length; offset += 8) {
		bytes.push(Number.parseInt(bits.slice(offset, offset + 8), 2));
	}
	return Buffer.from(bytes);
}

/**
 * RFC 6238 over RFC 4226, standing in for the authenticator app the enrolment expects. Enrolment is
 * the only way to reach the sign-in step that asks for a code, and it runs through the same two
 * endpoints a user's would.
 */
function authenticatorCode(base32Secret: string): string {
	const counter = Buffer.alloc(8);
	counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 1000 / TOTP_PERIOD_SECONDS)));

	const digest = createHmac('sha1', decodeBase32(base32Secret)).update(counter).digest();
	const offset = digest[digest.length - 1] & 0x0f;
	const truncated = digest.readUInt32BE(offset) & 0x7fff_ffff;
	return String(truncated % 10 ** TOTP_DIGITS).padStart(TOTP_DIGITS, '0');
}

async function enrolAuthenticator({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	const setup = await fetch('/api/auth/totp/setup', { method: 'POST' });
	const secret = stringField(setup, 'base32Secret');
	const challengeToken = stringField(setup, 'challengeToken');
	if (!secret || !challengeToken) throw new Error('TOTP setup returned no secret or challenge token');
	await fetch('/api/auth/totp/verify-setup', {
		method: 'POST',
		body: {
			challengeToken,
			code: authenticatorCode(secret),
			label: 'Authenticator app',
		},
	});
	return {};
}

/**
 * Reaches the authenticator step of sign-in, which exists only behind a passkey assertion the
 * account has actually completed. A CDP virtual authenticator registers a credential while the
 * owner is signed in, then signs in again with it; Chromium scopes that authenticator to the debug
 * session, so both halves have to happen on this page rather than in a context of their own.
 */
async function signInToTheAuthenticatorStep(page: Page): Promise<void> {
	const { origin } = new URL(page.url());
	const cdp = await page.context().newCDPSession(page);
	await cdp.send('WebAuthn.enable', { enableUI: false });
	await cdp.send('WebAuthn.addVirtualAuthenticator', {
		options: {
			protocol: 'ctap2',
			transport: 'internal',
			hasResidentKey: true,
			hasUserVerification: true,
			isUserVerified: true,
			automaticPresenceSimulation: true,
		},
	});

	// Standing up a signed-in browser is the one dev-only step here — the same one every owner
	// session takes. What follows is the app's own: a real passkey registration, then a real
	// assertion against it, which is what raises the challenge being photographed.
	await page.goto(`${origin}/api/v4/dev-only/auth/login?redirect=%2Fsettings%2Faccount`, {
		waitUntil: 'domcontentloaded',
	});
	await page.getByRole('button', { name: 'Add passkey' }).click();
	const skipLabel = page.getByRole('button', { name: 'Skip' });
	await skipLabel.click();
	// The dialog closes only once the credential is stored, so this is the registration's receipt.
	await skipLabel.waitFor({ state: 'detached' });

	await page.context().clearCookies();
	await page.goto(`${origin}/auth/login`, { waitUntil: 'domcontentloaded' });
	await page.getByTestId('passkey-sign-in').click();
	await page.getByText('Your passkey was accepted.').waitFor();
}

async function seededAlertRule(
	{ fetch }: ArrangeContext,
	name: string,
): Promise<Record<string, string>> {
	const rules = await fetch('/api/v4/alert-rules');
	const rule = Array.isArray(rules) ? rules.find((r) => stringField(r, 'name') === name) : undefined;
	const ruleId = stringField(rule, 'id');
	if (!ruleId) throw new Error(`the seeded tenant has no "${name}" alert rule`);
	return { ruleId };
}

const seededLowRule = (context: ArrangeContext) => seededAlertRule(context, 'Low');

interface WireCondition {
	type: string;
	[payload: string]: unknown;
}

const glucose = (direction: 'below' | 'above', value: number): WireCondition => ({
	type: 'threshold',
	threshold: { direction, value },
});

const inApp = { channelType: 'in_app' };

/**
 * Created disabled: the rules exist to be photographed, and one the engine evaluated against the
 * seeded readings could raise a live alert over every capture after it. The switch that shows it
 * is on the Identity card, which none of these entries photograph.
 */
async function createAlertRule(
	{ fetch }: ArrangeContext,
	rule: {
		name: string;
		severity: 'critical' | 'warning' | 'info';
		condition: WireCondition;
		channels?: Record<string, unknown>[];
		autoResolve?: WireCondition;
		clientConfiguration?: Record<string, unknown>;
	},
): Promise<Record<string, string>> {
	const { type, [type]: conditionParams } = rule.condition;
	const created = await fetch('/api/v4/alert-rules', {
		method: 'POST',
		body: {
			name: rule.name,
			severity: rule.severity,
			isEnabled: false,
			conditionType: type,
			conditionParams,
			channels: rule.channels ?? [inApp],
			autoResolveEnabled: rule.autoResolve !== undefined,
			autoResolveParams: rule.autoResolve,
			clientConfiguration: rule.clientConfiguration,
		},
	});
	const ruleId = stringField(created, 'id');
	if (!ruleId) throw new Error(`creating the "${rule.name}" alert rule returned no id`);
	return { ruleId };
}

// Left without a time zone, as the editor shows it once the zone is cleared back to the profile's,
// so the picture does not carry the capture browser's own zone.
const lowOvernight = (context: ArrangeContext) =>
	createAlertRule(context, {
		name: 'Low overnight',
		severity: 'warning',
		condition: {
			type: 'composite',
			composite: {
				operator: 'and',
				conditions: [
					{ type: 'sustained', sustained: { minutes: 20, child: glucose('below', 70) } },
					{ type: 'time_of_day', time_of_day: { from: '22:00', to: '07:00' } },
				],
			},
		},
		channels: [inApp, { channelType: 'web_push', destinationLabel: 'Bedroom laptop' }],
	});

const fallingTowardLow = (context: ArrangeContext) =>
	createAlertRule(context, {
		name: 'Falling toward low',
		severity: 'warning',
		condition: {
			type: 'composite',
			composite: {
				operator: 'or',
				conditions: [
					glucose('below', 70),
					{
						type: 'composite',
						composite: {
							operator: 'and',
							conditions: [
								glucose('below', 100),
								{ type: 'rate_of_change', rate_of_change: { direction: 'falling', rate: 2 } },
							],
						},
					},
				],
			},
		},
	});

// The docs' own auto-resolve example: one that closes the alert while it still holds. The inverse
// of the condition would close it no sooner than the condition itself does.
const highThatClearsItself = (context: ArrangeContext) =>
	createAlertRule(context, {
		name: 'High, until it is coming down',
		severity: 'warning',
		condition: glucose('above', 250),
		autoResolve: {
			type: 'sustained',
			sustained: { minutes: 15, child: { type: 'rate_of_change', rate_of_change: { direction: 'falling', rate: 1 } } },
		},
	});

const lowWithSmartSnooze = (context: ArrangeContext) =>
	createAlertRule(context, {
		name: 'Low',
		severity: 'warning',
		condition: glucose('below', 70),
		clientConfiguration: {
			snooze: {
				defaultMinutes: 15,
				options: [5, 15, 30, 60],
				maxCount: 3,
				smartSnooze: true,
				smartSnoozeExtendMinutes: 15,
				conditions: [{ type: 'trend', trend: { bucket: 'rising' } }],
			},
		},
	});

async function quietHours({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	await fetch('/api/v4/tenant-alert-settings', {
		method: 'PUT',
		body: {
			dndManualActive: true,
			dndScheduleEnabled: true,
			dndScheduleStart: '22:00:00',
			dndScheduleEnd: '07:00:00',
		},
	});
	return {};
}

/**
 * The replay starts playing by itself once its chart has data and sweeps the window over twelve
 * seconds, so only the playhead standing at the far end is a frame that comes out the same twice.
 */
async function replayPlayedThrough(page: Page): Promise<void> {
	await page.waitForFunction(
		() =>
			document.querySelector('[data-testid="playback-tick-strip"] line')?.getAttribute('x1') ===
			'100',
	);
}

/**
 * The screenshots the documentation embeds, by id. An id is a permanent handle: renaming one
 * breaks every page that already points at it, so add rather than rename.
 */
export const definitions: ScreenshotDefinition[] = [
	// First, so the browser has no signed-in context yet: an owner session open elsewhere in the
	// same browser leaves the share host answering with the sign-in page instead of the shared
	// dashboard.
	{
		id: 'share-anonymous-view',
		route: '{shareUrl}',
		scenario: 'patient',
		session: 'anonymous',
		arrange: openPublicShare,
		// Both the sign-in page and the shared dashboard are settled pages, so only the account
		// menu's signed-out state tells the runner it is photographing the right one.
		prepare: async (page) => {
			await page.getByTestId('sign-in-link').waitFor();
		},
		alt: 'What someone who opens your public link sees without signing in: the same home screen with the latest reading, the graph and the summary panels, and a Sign in button where your own account menu would be.',
	},
	{
		id: 'dashboard-overview',
		route: '/',
		scenario: 'patient',
		alt: 'The Nocturne home screen. A large number shows the most recent glucose reading with an arrow for which way it is heading, and a graph underneath plots the readings from the last few hours alongside markers for insulin doses and meals.',
	},
	{
		id: 'first-run',
		route: '/',
		scenario: 'first-run',
		alt: 'The Nocturne home screen on a brand new site, before any device or app has sent readings. The graph is empty and every panel reads zero or "No data available".',
	},
	{
		id: 'connect-data-source',
		route: '/setup/connect',
		scenario: 'first-run',
		alt: 'The Connect a Data Source step of setup. Each service Nocturne can collect readings from, and each phone app that can send readings to it, is listed as a tile you pick from.',
	},
	// Both clip to a single card because at docs-column width a page-wide shot of this route
	// shrinks the credentials boxes past the point where the callouts over them can be read.
	{
		id: 'connector-dexcom-credentials',
		route: '/settings/connectors/dexcom',
		scenario: 'patient',
		clip: '[data-testid="connector-credentials"]',
		alt: 'The Credentials panel of the Dexcom connection page, holding the boxes for the Dexcom Share username and password that Nocturne signs in with.',
		anchors: {
			username: '[data-testid="connector-credentials"] input:not([type="password"])',
			password: '[data-testid="connector-credentials"] input[type="password"]',
		},
	},
	{
		id: 'connector-dexcom-enable',
		route: '/settings/connectors/dexcom',
		scenario: 'patient',
		clip: '[data-testid="connector-enable"]',
		alt: 'The Enable Connector card of the Dexcom connection page, with a switch that turns collection on or off.',
	},
	{
		id: 'carb-entry-edit',
		route: '/reports/treatments',
		scenario: 'patient',
		// The Carbs tab first, so the row opened is a carb entry rather than whatever the seeded
		// data happens to have logged most recently.
		prepare: async (page) => {
			await page.getByRole('tab', { name: /Carbs/ }).click();
			await page.getByTestId('treatment-row').first().click();
			await page.getByLabel('Absorption Time (min)').waitFor();
		},
		clip: '[data-testid="treatment-edit-dialog"]',
		// The amount and the time come from the seeded data, so this one image differs every capture.
		alt: 'The Edit Record box for a carb entry. A line across the top gives when it was recorded and which app and device sent it; under that are the date and time, the grams of carbohydrate, and boxes for absorption time and carb time, both left empty by a source that reported neither. A Linked Records list at the bottom shows the other records written as part of the same event.',
	},
	{
		id: 'food-catalog',
		route: '/food',
		scenario: 'patient',
		// The list loads itself after hydration rather than through the route, and a settled empty
		// card looks exactly like a settled full one to the runner. A row is the proof the page is
		// finished, and without it a capture can come back reading "Loading food database".
		prepare: async (page) => {
			await page.getByTestId('food-row').first().waitFor();
		},
		alt: 'The Food Editor page, listing the foods saved on this instance. A search box, a Favorites filter and a sort control sit across the top, with chips underneath for filtering by category and by glycaemic index, and then one row per food showing its name, its carbs and the portion those carbs are for.',
		anchors: {
			search: '[data-testid="food-search"]',
			favorites: '[data-testid="food-favorites-filter"]',
			sort: '[data-testid="food-sort"]',
		},
	},
	{
		id: 'food-composer',
		route: '/food',
		scenario: 'patient',
		// Opened with its extra fields showing, because the collapsed form is four boxes and the
		// docs page it sits under is a table of every field a food holds.
		prepare: async (page) => {
			await openWith(page.getByRole('button', { name: 'Add food' }), page.getByTestId('food-composer-details'));
			await page.getByTestId('food-composer-details').click();
			await page.getByLabel('Energy').waitFor();
		},
		clip: '[data-testid="food-composer"]',
		alt: 'The Add food form, expanded. The top row takes the name, the carbs, the portion those carbs are for, the unit that portion is measured in, and whether the food is low, medium or high GI. Underneath it, a second row adds fat, protein, energy in kilocalories, and a category and subcategory to group the food under.',
	},
	{
		id: 'deduplication-card',
		route: '/settings/data-quality',
		scenario: 'patient',
		clip: '[data-testid="deduplicate-records"]',
		alt: 'The Deduplicate Records tool, under Data Maintenance on the Data Quality settings page. It explains that it links records from different data sources that describe the same event, and offers a Run Deduplication button.',
	},
	{
		id: 'alerts-configuration',
		route: '/alerts',
		alt: 'The Alerts page. Three tiles across the top count how many rules are switched on, how many alerts are sounding right now, and how many fired this week. Below them sits the list of rules: an urgent low, a low, a high, and one for the sensor going quiet, each showing the reading it watches for, a switch to turn it off, and a button to send a test alert. A New rule button sits in the top corner.',
	},
	{
		id: 'report-agp',
		route: '/reports/agp',
		scenario: 'patient',
		alt: 'The Ambulatory Glucose Profile report. It stacks every day of the chosen date range onto one 24-hour graph, drawing a middle line with shaded bands around it, and lists the share of time spent in each glucose range beside it.',
	},
	{
		id: 'sharing-settings',
		route: '/settings/members',
		scenario: 'patient',
		alt: 'The top of the Sharing and Privacy settings page. The Public access card fills the screen: a switch for whether anyone with the link can view your data, a tile for each kind of data, and a choice of how far back. Invites, guest links and your list of members follow further down the page.',
	},
	{
		id: 'settings-overview',
		route: '/settings',
		scenario: 'patient',
		alt: 'The main Settings page, a grid of cards linking to each group of settings: your account, your data sources, sharing, alerts, appearance and more.',
	},
	{
		id: 'sharing-public-link',
		route: '/settings/members',
		scenario: 'patient',
		arrange: openPublicShare,
		prepare: settledPublicLink,
		clip: '[data-testid="public-access-card"]',
		alt: 'The Public access card, switched on. The address is hidden behind dots, with buttons to show it, copy it, and regenerate it beside them, then a tile for each kind of data you can share or keep back, a choice between all history and the last 24 hours, and a sentence spelling out what a viewer would see.',
		anchors: {
			enable: '[data-testid="public-access-toggle"]',
			'time-window': '[data-testid="public-access-window"]',
		},
	},
	{
		id: 'sharing-invite-card',
		route: '/settings/members',
		scenario: 'patient',
		prepare: async (page) => {
			await openWith(page.getByRole('button', { name: 'Create Invite Link' }), page.getByTestId('create-invite-card'));
		},
		clip: '[data-testid="create-invite-card"]',
		alt: 'The Create Invite Link card. You can name the invite, tick the roles the person should have, choose how long the link stays usable, and limit them to the last 24 hours of data before pressing Create Link.',
	},
	{
		id: 'sharing-guest-links',
		route: '/settings/members',
		scenario: 'patient',
		arrange: inviteAGuest,
		clip: '[data-testid="guest-links"]',
		alt: 'The Temporary Guest Links section, with one link made for a school nurse. It is marked Pending because nobody has used the code yet, and shows when it was created, when it expires, and a Revoke button.',
	},
	{
		id: 'guest-code-entry',
		route: '/guest',
		scenario: 'first-run',
		session: 'anonymous',
		clip: '[data-testid="guest-code-card"]',
		alt: 'The guest code page. Someone you have sent a code to types it into a single box and presses Access Data; the page explains the code works once and keeps that device signed in for 48 hours.',
	},
	{
		id: 'clock-list',
		route: '/clock',
		scenario: 'patient',
		alt: 'The Clock page, listing the clock faces saved on this instance. One called Bedside Clock is shown as a card: a small live copy of the face on top, then the date it was last changed and buttons to edit it or open it full screen. A New Clock button sits in the page header.',
	},
	{
		id: 'clock-example',
		route: '/clock/{clockId}',
		scenario: 'patient',
		session: 'anonymous',
		// The face sizes itself to the screen, so a desktop frame leaves the reading a speck in a
		// field of black; a phone is both the honest device for it and a legible picture. Clipped
		// to the face's own rows because even a phone frame is mostly the empty screen around it,
		// which at docs-column width shrinks the reading past legibility.
		viewport: 'mobile',
		clip: '[data-testid="clock-face-rows"]',
		arrange: seededClockFace,
		// The reading and its trend come from the seeded data, so this one image differs every capture.
		alt: 'A clock face as it looks on a phone or tablet left by the bed: the latest glucose reading in large digits, an arrow for which way it is heading, and the change since the reading before it underneath.',
	},
	{
		id: 'clock-builder',
		route: '/clock/config/{clockId}',
		scenario: 'patient',
		arrange: seededClockFace,
		alt: 'The clock face editor. The face fills the canvas showing the reading it will display, with a plus button above and below it for adding another row of information, and a toolbar across the top to undo, save and preview.',
	},
	{
		id: 'sign-in',
		route: '/auth/login',
		scenario: 'first-run',
		session: 'anonymous',
		clip: '[data-testid="sign-in-card"]',
		alt: 'The Nocturne sign-in card. Sign in with passkey is the main button, with a username option and a recovery code link under it, and a Request membership link at the bottom for someone who has not been given access yet.',
		anchors: {
			passkey: '[data-testid="passkey-sign-in"]',
			'request-membership': '[data-testid="request-membership-link"]',
		},
	},
	{
		id: 'request-membership-dialog',
		route: '/auth/login',
		scenario: 'first-run',
		session: 'anonymous',
		prepare: async (page) => {
			await page.getByTestId('request-membership-link').click();
			await page.getByTestId('request-membership-dialog').waitFor();
		},
		clip: '[data-testid="request-membership-dialog"]',
		alt: 'The Request Membership box. You write a short note introducing yourself to the site owner, up to 500 characters, then press Continue to Sign Up.',
	},
	{
		id: 'totp-setup',
		route: '/settings/account',
		scenario: 'patient',
		prepare: async (page) => {
			await page.getByRole('button', { name: 'Add authenticator' }).click();
			await page.getByTestId('totp-setup-dialog').waitFor();
		},
		clip: '[data-testid="totp-setup-dialog"]',
		// The QR code and the secret are minted per run, so this one image differs every capture.
		alt: 'The Set up authenticator app box. It shows a QR code to scan with an authenticator app, the same secret written out for typing in by hand, a place to name the app, and six boxes for the code it gives back.',
	},
	{
		id: 'totp-challenge',
		route: '/auth/login',
		scenario: 'first-run',
		// Its prepare signs a browser in. On the shared anonymous context that session would
		// outlive the entry and every later anonymous capture would be photographing a signed-in
		// browser instead of a stranger's.
		session: 'isolated',
		arrange: enrolAuthenticator,
		prepare: signInToTheAuthenticatorStep,
		clip: '[data-testid="sign-in-card"]',
		alt: 'The second step of signing in on an account that uses an authenticator app. Nocturne says the passkey was accepted and asks for the current six-digit code before it will finish signing you in.',
	},
	// The alert entries come last, and among them the ones that create rules follow the ones that
	// read the seeded set: an arranged rule would otherwise join the alerts list, the simulator's
	// replay and the lookup of the seeded Low by name. The tracker entries arrange rules of their own
	// (a tracker's thresholds), so they sit with those. Do Not Disturb is last of all because it
	// silences every alert after it.
	{
		id: 'alert-rule-editor',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: seededLowRule,
		// The historic firings are the seeded alarm history, so this one image differs every capture.
		alt: 'The page for editing one alert rule, here the Low rule. The main column starts with an Identity card for its name, description and how serious it is, then a Condition card that reads Notify when all of these are true, with one line saying glucose below 70. Down the right side, a Test alert panel offers Fire saved rule and Replay against history, and under it a list of the times this rule has actually gone off.',
	},
	{
		id: 'alert-rule-identity',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: seededLowRule,
		clip: '[data-testid="alert-identity-card"]',
		alt: 'The Identity card of an alert rule. It holds boxes for the name of the rule and an optional description, a Severity menu set to Warning, an Enabled switch in the top corner, and a tick box for Allow through Do Not Disturb with a note that critical rules always get through.',
		anchors: {
			severity: '[data-testid="alert-severity"]',
			'allow-dnd': '[data-testid="alert-allow-dnd"]',
			enabled: '[data-testid="alert-enabled"]',
		},
	},
	{
		id: 'alert-rule-add-picker',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: seededLowRule,
		prepare: async (page) => {
			await page
				.getByTestId('alert-condition-card')
				.getByTestId('alert-add-condition')
				.click();
			await page.getByTestId('alert-add-picker').waitFor();
		},
		clip: '[data-testid="alert-add-picker"]',
		alt: 'The list that opens from Add condition, grouped under headings. The top of it shows the glucose conditions: Glucose, Glucose bucket, Predicted glucose, Rate of change, Trend and Sensor stale, each with a coloured icon and a line saying what it checks, with the insulin group starting below.',
	},
	{
		id: 'alerts-history',
		route: '/alerts/history',
		scenario: 'patient',
		// Every row is the seeded alarm history, so this one image differs every capture.
		alt: 'The Alert history page. Under Recent fires, each row names the rule that went off with a coloured dot and a label for how serious it was, marks the ones someone acknowledged, and gives when the alert started and ended and how long it lasted.',
	},
	{
		id: 'alerts-simulator',
		route: '/alerts/simulator',
		scenario: 'patient',
		prepare: replayPlayedThrough,
		// The replay covers the last 24 hours of seeded readings, so this one image differs every
		// capture.
		alt: 'The Simulator page after a replay of the last 24 hours. A glucose graph fills the top with a marker wherever an alert would have gone off, a playback strip under it shows each event as a tick, a list names every alert and the time it would have fired, and a panel beside them lists your rules.',
	},
	{
		id: 'alert-rule-sustained-low',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: lowOvernight,
		clip: '[data-testid="alert-condition-card"]',
		alt: 'The Condition card of a rule called Low overnight. It reads Notify when all of these are true, then two lines: glucose below 70 for at least 20 minutes, and a time of day from 10:00 PM to 07:00 AM in the time zone on the patient record.',
		anchors: {
			operator: '[data-testid="alert-condition-card"] [data-testid="alert-operator-toggle"]',
			sustained: '[data-testid="alert-condition-card"] [data-testid="alert-sustained-minutes"]',
		},
	},
	{
		id: 'alert-rule-nested-group',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: fallingTowardLow,
		clip: '[data-testid="alert-condition-card"]',
		alt: 'The Condition card of a rule called Falling toward low. It reads Notify when any of these are true, then a line for glucose below 70, then an indented group box matching all of two lines inside it: glucose below 100, and a rate of change falling at least 2 mg/dL a minute.',
		anchors: {
			group: '[data-testid="alert-condition-card"] [data-testid="alert-condition-group"]',
		},
	},
	{
		id: 'alert-rule-row-actions',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: fallingTowardLow,
		// The card sits low on the page, where the menu has no room below the row and opens upwards,
		// out of the clip. Centred it opens downwards, over the card, and clear of the sticky banner
		// that would cover a card scrolled to the very top.
		prepare: async (page) => {
			const card = page.getByTestId('alert-condition-card');
			await card.evaluate((el) => el.scrollIntoView({ block: 'center' }));
			await card.getByTestId('alert-row-actions').first().click();
			await page.getByTestId('alert-row-actions-menu').waitFor();
		},
		clip: '[data-testid="alert-condition-card"]',
		alt: 'The actions menu opened from the three-dot button at the end of a condition line. It offers Wrap in AND group, Wrap in OR group, Wrap in NOT, Make sustained, and Remove.',
	},
	{
		id: 'alert-rule-channels',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: lowOvernight,
		clip: '[data-testid="alert-channels-card"]',
		alt: 'The Channels card of an alert rule, listing where the alert is sent. An In-App entry notes it is routed to your account automatically, a Browser Push entry carries the label Bedroom laptop, and an Add channel button sits underneath.',
	},
	{
		id: 'alert-rule-auto-resolve',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: highThatClearsItself,
		clip: '[data-testid="alert-auto-resolve-card"]',
		alt: 'The Auto-resolve card switched on, with a Suggest button beside the switch. Its condition reads Notify when all of these are true, with one line: rate of change falling at least 1 mg/dL a minute, for at least 15 minutes.',
	},
	{
		id: 'alert-rule-smart-snooze',
		route: '/alerts/{ruleId}',
		scenario: 'patient',
		arrange: lowWithSmartSnooze,
		clip: '[data-testid="alert-smart-snooze-card"]',
		alt: 'The Smart snooze card switched on. It extends a snooze by 15 minutes at a time while its condition holds, here glucose trending upward, and explains which alerts are extended when no condition is set.',
	},
	{
		id: 'trackers-active',
		route: '/settings/trackers',
		scenario: 'patient',
		clip: '[data-testid="active-trackers"]',
		// Ages and start times come from the seeded device-change schedule, so this image differs every capture.
		alt: 'The Active tab of the trackers page. Each running tracker is a row: a CGM sensor, an infusion site, an insulin reservoir and a pump battery, each with the time left before it is due in large type, how old it is and when it was started. Every row has a Complete button and a delete button, the reservoir row also has Record Level, and a Start Tracker menu sits in the top corner.',
	},
	{
		id: 'tracker-pill-bar',
		route: '/',
		scenario: 'patient',
		// The bar lays its pills straight into this row (display: contents), so the row is the clip.
		clip: '[data-testid="status-pills"]',
		anchors: { trackers: '[data-testid="tracker-pill-bar"] button' },
		// Ages and the loop's figures come from the seeded data, so this image differs every capture.
		alt: 'The row of status pills beside the current reading on the home screen. After the pills your pump and loop report comes one pill per running tracker, each giving its name and how long it has been running, with a thin line underneath showing how much of its expected life is used up.',
	},
	{
		id: 'tracker-pill-popover',
		route: '/',
		scenario: 'patient',
		prepare: async (page) => {
			await openWith(
				page.getByTestId('tracker-pill-bar').getByRole('button').first(),
				page.getByTestId('tracker-pill-popover'),
			);
		},
		clip: '[data-testid="tracker-pill-popover"]',
		// Running time, time remaining and the start time come from the seeded schedule, so this image differs every capture.
		alt: 'The panel that opens when you tap a tracker pill. It shows how long the tracker has been running, its expected lifespan, the time remaining and when it was started, with a Complete Tracker button at the bottom.',
	},
	{
		id: 'tracker-editor',
		route: '/settings/trackers',
		scenario: 'patient',
		arrange: sensorTrackerWithThresholds,
		prepare: openThresholdTrackerEditor,
		clip: '[data-testid="tracker-editor"]',
		alt: 'The Edit Definition box for a tracker. It asks for a name and a category, an optional description, whether the tracker runs for a length of time or is booked for a date, and the expected lifespan, here 240 hours. Under that is the list of device events that restart the tracker automatically, with Sensor Start ticked.',
	},
	{
		id: 'tracker-triggers',
		route: '/settings/trackers',
		scenario: 'patient',
		arrange: sensorTrackerWithThresholds,
		prepare: async (page) => {
			await openThresholdTrackerEditor(page);
			await page.getByTestId('tracker-triggers').scrollIntoViewIfNeeded();
		},
		clip: '[data-testid="tracker-triggers"]',
		alt: 'The Restart automatically on section of the tracker editor. It lists the device events a tracker can restart on, from Sensor Start to Pump Battery Change, each with a tick box; Sensor Start is ticked. Under the list is an optional box for words the event notes must contain.',
	},
	{
		id: 'tracker-thresholds',
		route: '/settings/trackers',
		scenario: 'patient',
		arrange: sensorTrackerWithThresholds,
		prepare: async (page) => {
			await openThresholdTrackerEditor(page);
			await page.getByTestId('tracker-thresholds').scrollIntoViewIfNeeded();
		},
		clip: '[data-testid="tracker-thresholds"]',
		alt: 'The Notification Thresholds list inside the tracker editor, with three steps: an Info notice a day before the sensor ends, a Warning two hours before, and an Urgent alert once it has run its full ten days. Each step has a level, a number of hours, a Channels button for choosing where it is delivered, and a message on the line below.',
	},
	{
		id: 'tracker-complete-dialog',
		route: '/settings/trackers',
		scenario: 'patient',
		prepare: async (page) => {
			await openWith(page.getByTestId('tracker-complete').first(), page.getByTestId('tracker-completion-dialog'));
		},
		clip: '[data-testid="tracker-completion-dialog"]',
		// The completion time defaults to now, so this image differs every capture.
		alt: 'The Complete box for a tracker. It asks when the change happened, the reason it ended, and optional notes, with a tick box to start a fresh one straight away.',
	},
	{
		id: 'alerts-dnd',
		route: '/alerts/dnd',
		scenario: 'patient',
		arrange: quietHours,
		alt: 'The Do Not Disturb page. The Manual card has its switch on, with an optional box for when it should switch itself off. The Schedule card has quiet hours switched on from 10:00 PM to 07:00 AM, with a note that the times follow the time zone on your patient record.',
	},
];
