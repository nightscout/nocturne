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
		alt: 'The Nocturne home screen on a brand new site, before any device or app has sent readings. The graph area is empty and the page points to where you set up a data source.',
	},
	{
		id: 'connect-data-source',
		route: '/setup/connect',
		scenario: 'first-run',
		alt: 'The Connect a Data Source step of setup. Each service Nocturne can collect readings from, and each phone app that can send readings to it, is listed as a tile you pick from.',
	},
	{
		id: 'connector-dexcom',
		route: '/settings/connectors/dexcom',
		scenario: 'patient',
		// The credentials panel sits below the fold; the callouts need the whole form in frame.
		fullPage: true,
		alt: 'The Dexcom connection page. A switch turns the connection on or off, and a Credentials panel holds the boxes for the Dexcom Share username and password that Nocturne signs in with.',
		anchors: {
			status: '[data-testid="connector-header"]',
			'enable-switch': '[data-testid="connector-enable"]',
			credentials: '[data-testid="connector-credentials"]',
		},
	},
	{
		id: 'alerts-configuration',
		route: '/alerts',
		// 'patient' once /alerts can render seeded data without pegging the renderer.
		scenario: 'first-run',
		alt: 'The Alerts page of a newly created site, showing the default alert rules ready to be switched on and configured.',
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
