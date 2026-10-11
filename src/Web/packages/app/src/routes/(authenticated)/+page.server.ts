import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { getOriginalProto } from '$lib/server/request-host';
import { resolveSingleTenantLanding } from '$lib/utils/tenant-host';
import { classifyRequestHost } from '$lib/server/tenantless-host';
import { transformChartData, type TransformedChartData } from '$lib/utils/chart-data-transform';

// Hours of data for initial fast load (most recent)
const INITIAL_HOURS = 6;
// Total hours to fetch (matches GLUCOSE_CHART_FETCH_HOURS)
const TOTAL_HOURS = 48;

export const load: PageServerLoad = async ({ locals, request, parent }) => {
	const { apiClient } = locals;

	// Only the apex and a reserved dashboard slug can be tenantless, so on any other host the
	// chart fetches start now instead of behind the layout chain's status and onboarding calls.
	// A viewer the layout will redirect to sign-in is left out, so it spends no chart queries.
	const { kind } = classifyRequestHost(request);
	const chartData =
		kind !== 'apex' && kind !== 'dashboard-slug' && (locals.isAuthenticated || locals.isShareHost)
			? loadChartData(apiClient)
			: null;

	const { tenantless, baseDomain, dashboardSlugs } = await parent();

	// A tenantless host serves the cross-tenant overview instead of one tenant's dashboard, so
	// there is no tenant whose chart data could be loaded here. The overview itself is fetched
	// client-side by the same remote query /tenants uses.
	if (tenantless) {
		const landing = resolveSingleTenantLanding(
			await getTenantMemberships(apiClient),
			baseDomain,
			getOriginalProto(request) + ':',
			dashboardSlugs
		);
		if (landing) throw redirect(303, landing);

		return { initialChartData: null };
	}

	const { initialChartData, initialWindowStart, historicalDataPromise } =
		chartData ?? loadChartData(apiClient);

	return {
		initialChartData: await initialChartData,
		initialWindowStart,
		streamed: {
			historicalChartData: historicalDataPromise,
		},
	};
};

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
 * The tenants this subject belongs to, or an empty list if the list cannot be fetched.
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
async function getTenantMemberships(apiClient: App.Locals['apiClient']) {
	try {
		return await apiClient.myTenants.getMyTenants();
	} catch (err) {
		console.error('Error loading the tenant list:', err);
		return [];
	}
}
