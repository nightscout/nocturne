import { createHmac } from 'node:crypto';
import type { Page } from '@playwright/test';
import type { ArrangeContext, ScreenshotDefinition } from './types.js';

/** Rich enough that the public view is worth a screenshot; still short of everything on offer. */
const SHARED_CATEGORIES = ['glucose.read', 'treatments.read', 'devices.read'];

/**
 * Turns the tenant's public link on, widens what it shows, and hands back the link itself — the one
 * moment the URL is knowable, since the server keeps only its digest.
 */
async function openPublicShare({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	const rotated = await fetch<{ url: string | null }>('/api/v4/share/rotate', { method: 'POST' });
	await fetch('/api/v4/share/scopes', { method: 'PUT', body: { scopes: SHARED_CATEGORIES } });
	await fetch('/api/v4/share/full-history', { method: 'PUT', body: { fullHistory: true } });

	if (!rotated.url) throw new Error('rotating the share link returned no URL');
	return { shareUrl: rotated.url };
}

/**
 * The card can show the link's address exactly once — at the moment the link is minted, since the
 * server keeps only its digest — so a share arranged through the API alone is photographed with an
 * empty link field. Regenerate mints one from the browser and is the card's own answer to "I no
 * longer have the address", which makes it the one control that reaches this state whether or not
 * public access was already on: the enable switch would only do it on a share that is currently
 * off, and both themes photograph the same tenant.
 */
async function revealThePublicLink(page: Page): Promise<void> {
	const regenerate = page.getByRole('button', { name: 'Regenerate' });
	await regenerate.click();
	await page.getByText('Regenerating invalidates the current link immediately.').waitFor();
	// Confirming adds a second Regenerate after the first in the document.
	await regenerate.last().click();
	// Copy is offered only while the card is holding an address it can copy, so it is the receipt
	// for a link that is actually in the frame.
	await page.getByRole('button', { name: 'Copy', exact: true }).waitFor();
}

async function inviteAGuest({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	await fetch('/api/v4/guest-links', { method: 'POST', body: { label: 'School nurse' } });
	return {};
}

async function seededClockFace({ fetch }: ArrangeContext): Promise<Record<string, string>> {
	const [face] = await fetch<{ id: string }[]>('/api/v4/clockfaces');
	if (!face) throw new Error('the seeded tenant has no clock face');
	return { clockId: face.id };
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
	const setup = await fetch<{ base32Secret: string; challengeToken: string }>(
		'/api/auth/totp/setup',
		{ method: 'POST' },
	);
	await fetch('/api/auth/totp/verify-setup', {
		method: 'POST',
		body: {
			challengeToken: setup.challengeToken,
			code: authenticatorCode(setup.base32Secret),
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
		id: 'alerts-configuration',
		route: '/alerts',
		// 'patient' once /alerts can render seeded data without pegging the renderer.
		scenario: 'first-run',
		alt: 'The Alerts page of a newly created site. It has no alert rules yet, and offers a New rule button to add the first one.',
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
		prepare: revealThePublicLink,
		clip: '[data-testid="public-access-card"]',
		// The address is minted per run, so this one image differs every capture.
		alt: 'The Public access card, switched on and holding a freshly minted link. The address is spelled out in full — the one time Nocturne shows it — with Copy and Regenerate beside it, then a tile for each kind of data you can share or keep back, a choice between all history and the last 24 hours, and a sentence spelling out what a viewer would see.',
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
			await page.getByRole('button', { name: 'Create Invite Link' }).click();
			await page.getByTestId('create-invite-card').waitFor();
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
];
