import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { getOriginalProto } from '#lib/server/request-host.js';
import { resolveSingleTenantLanding } from '#lib/utils/tenant-host.js';
import { classifyRequestHost } from '#lib/server/tenantless-host.js';
import { transformChartData, type TransformedChartData } from '#lib/utils/chart-data-transform.js';
import { hasPlotSize, renderPlotSnapshot } from '#lib/server/plot-snapshot.js';
import { mergeChartData } from '#lib/utils/chart-data-merge.js';
import { displayPreferenceLayers, displayPreferenceSources } from '#lib/server/display-preference-layers.js';
import { showsPredictions } from '#lib/types/dashboard-widgets.js';

// Hours of data for initial fast load (most recent)
const INITIAL_HOURS = 6;
// Total hours to fetch (matches GLUCOSE_CHART_FETCH_HOURS)
const TOTAL_HOURS = 48;
// The live chart's window when the tenant sets none (focusHours in +page.svelte).
const DEFAULT_LOOKBACK_HOURS = 12;

export const load: PageServerLoad = async ({ locals, request, parent, cookies, isDataRequest }) => {
	const { apiClient } = locals;

	// Only the apex and a reserved dashboard slug can be tenantless, so on any other host the
	// chart fetches start now instead of behind the layout chain's status and onboarding calls.
	// A viewer the layout will redirect to sign-in is left out, so it spends no chart queries.
	const { kind } = classifyRequestHost(request);
	const maybeTenantless = kind === 'apex' || kind === 'dashboard-slug';
	const chartData =
		!maybeTenantless && (locals.isAuthenticated || locals.isShareHost)
			? loadChartData(apiClient)
			: null;
	// A document's first render draws the chart as it will stay: the window the tenant set and
	// the forecast's room, not the defaults the client queries would correct a second later. A
	// client navigation reads them from those queries' cache.
	const chartSettings = chartData && !isDataRequest ? loadChartSettings(apiClient) : null;

	// `parent()` in a server data request reruns every ancestor server load, invalidated or not,
	// so a client navigation to the dashboard would repeat the layouts' API calls. Only a host
	// that can be tenantless needs their answer.
	const { tenantless, baseDomain, dashboardSlugs } = maybeTenantless
		? await parent()
		: { tenantless: false, baseDomain: null, dashboardSlugs: [] };

	// A tenantless host serves the cross-tenant overview instead of one tenant's dashboard, so
	// there is no tenant whose chart data could be loaded here. The overview itself is fetched
	// client-side by the same remote query /tenants uses.
	if (tenantless) {
		const landing = resolveSingleTenantLanding(
			await getReachableTenants(apiClient),
			baseDomain,
			getOriginalProto(request) + ':',
			dashboardSlugs
		);
		if (landing) throw redirect(303, landing, { external: true });

		return { initialChartData: null };
	}

	const loaded = chartData ?? (maybeTenantless ? loadChartData(apiClient) : null);
	if (!loaded) return { initialChartData: null };
	const { initialChartData, initialWindowStart, historicalDataPromise } = loaded;
	const [initialData, settings] = await Promise.all([initialChartData, chartSettings]);

	// A share or guest viewer reads the owner's presentation, which this load does not fetch.
	// The live chart first draws both windows merged, so the snapshot waits for the second: the
	// API answers either in the same time, and both are in this document anyway.
	const plotSnapshot =
		settings && !locals.isShareHost && !locals.isGuestSession && hasPlotSize(cookies)
			? renderPlotSnapshot({
					chartData: initialData && mergeChartData(initialData, await historicalDataPromise),
					nowMs: Date.now(),
					lookbackHours: settings.uiSettings?.features?.display?.focusHours ?? DEFAULT_LOOKBACK_HOURS,
					predictions:
						settings.predictionsAvailable === true && showsPredictions(settings.uiSettings),
					cookies,
					preferenceLayers: displayPreferenceLayers(displayPreferenceSources(locals, cookies, null)),
				})
			: null;

	return {
		initialChartData: initialData,
		initialUiSettings: settings?.uiSettings ?? null,
		initialPredictionsAvailable: settings?.predictionsAvailable ?? null,
		plotSnapshot,
		initialWindowStart,
		streamed: {
			historicalChartData: historicalDataPromise,
		},
	};
};

/**
 * The tenant's dashboard settings and whether forecasts can be made, as the page's `getUiSettings`
 * and the chart's `getPredictionStatus` would answer. Each is null when it cannot be read; the
 * client queries then decide, as before.
 */
async function loadChartSettings(apiClient: App.Locals['apiClient']) {
	const [uiSettings, predictionsAvailable] = await Promise.all([
		apiClient.uiSettings.getUISettings().catch((err: unknown) => {
			console.error('Error loading UI settings:', err);
			return null;
		}),
		apiClient.predictions
			.getStatus()
			.then((status) => status.available ?? false)
			.catch((err: unknown) => {
				console.error('Error checking prediction status:', err);
				return null;
			}),
	]);
	return { uiSettings, predictionsAvailable };
}

/**
 * Starts the 6h initial window and the 48h historical window concurrently. Each settles to null
 * on failure, so a promise left unawaited by a redirect never rejects unhandled.
 */
function loadChartData(apiClient: App.Locals['apiClient']) {
	const now = Date.now();
	const intervalMs = 5 * 60 * 1000;

	// Calculate time boundaries
	const endTime = Math.ceil(now / intervalMs) * intervalMs;
	const initialStartTime = endTime - INITIAL_HOURS * 60 * 60 * 1000;
	const fullStartTime = endTime - TOTAL_HOURS * 60 * 60 * 1000;

	// Started before the initial window is awaited so both pipelines run concurrently.
	const historicalDataPromise = (async (): Promise<TransformedChartData | null> => {
		try {
			const data = await apiClient.chartData.getDashboardChartData(
				fullStartTime,
				initialStartTime,
				5,
				false
			);
			return transformChartData(data);
		} catch (err) {
			console.error('Error loading historical chart data:', err);
			return null;
		}
	})();

	const initialChartData = (async (): Promise<TransformedChartData | null> => {
		try {
			const data = await apiClient.chartData.getDashboardChartData(initialStartTime, endTime, 5, false);
			return transformChartData(data);
		} catch (err) {
			console.error('Error loading initial chart data:', err);
			return null;
		}
	})();

	return { initialChartData, initialWindowStart: initialStartTime, historicalDataPromise };
}

/**
 * The tenants this subject can go to, or an empty list if the list cannot be fetched.
 *
 * The bare list, not the overview: all that is wanted here is how many there are and what the
 * sole one is called, and the overview aggregates each tenant's latest glucose to answer that.
 * TenantsOverview fetches the aggregate itself once the page renders.
 *
 * The overview is further narrowed to tenants the current token can read glucose from, which no
 * field of TenantDto can express, so this list cannot match it.
 *
 * A failure here must not block the dashboard: it only costs the single-tenant shortcut.
 */
async function getReachableTenants(apiClient: App.Locals['apiClient']) {
	try {
		return await apiClient.myTenants.getMyTenants(true);
	} catch (err) {
		console.error('Error loading the tenant list:', err);
		return [];
	}
}
