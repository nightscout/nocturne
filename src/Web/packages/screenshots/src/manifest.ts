import type { ScreenshotDefinition } from './types.js';

/**
 * The screenshots the documentation embeds, by id. An id is a permanent handle: renaming one
 * breaks every page that already points at it, so add rather than rename.
 */
export const definitions: ScreenshotDefinition[] = [
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
		alt: 'The Sharing and Privacy settings page, where you choose whether anyone with the link can view your data, and see the list of people you have invited along with what each of them is allowed to do.',
	},
	{
		id: 'settings-overview',
		route: '/settings',
		scenario: 'patient',
		alt: 'The main Settings page, a grid of cards linking to each group of settings: your account, your data sources, sharing, alerts, appearance and more.',
	},
];
