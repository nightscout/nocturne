import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { getOriginalProto } from '$lib/server/request-host';
import { resolveSingleTenantLanding } from '$lib/utils/tenant-host';
import { transformChartData, type TransformedChartData } from '$lib/utils/chart-data-transform';

// Hours of data for initial fast load (most recent)
const INITIAL_HOURS = 6;
// Total hours to fetch (matches GLUCOSE_CHART_FETCH_HOURS)
const TOTAL_HOURS = 48;

export const load: PageServerLoad = async ({ locals, request, parent }) => {
	const { apiClient } = locals;

	const { tenantless, baseDomain, dashboardSlugs } = await parent();

	// A tenantless host serves the cross-tenant overview instead of one tenant's dashboard, so
	// there is no tenant whose chart data could be loaded here. The overview itself is fetched
	// client-side by the same remote query /tenants uses.
	if (tenantless) {
		const landing = resolveSingleTenantLanding(
			await getAccessibleTenants(apiClient),
			baseDomain,
			getOriginalProto(request) + ':',
			dashboardSlugs
		);
		if (landing) throw redirect(303, landing);

		return { tenantless: true, initialChartData: null };
	}

	const now = Date.now();
	const intervalMs = 5 * 60 * 1000;

	// Calculate time boundaries
	const endTime = Math.ceil(now / intervalMs) * intervalMs;
	const initialStartTime = endTime - INITIAL_HOURS * 60 * 60 * 1000;
	const fullStartTime = endTime - TOTAL_HOURS * 60 * 60 * 1000;

	// Fetch initial recent data immediately (blocking)
	let initialChartData: TransformedChartData | null = null;
	try {
		const data = await apiClient.chartData.getDashboardChartData(initialStartTime, endTime, 5);
		initialChartData = transformChartData(data);
	} catch (err) {
		console.error('Error loading initial chart data:', err);
	}

	// Create a promise for historical data that will stream in
	const historicalDataPromise = (async (): Promise<TransformedChartData | null> => {
		try {
			const data = await apiClient.chartData.getDashboardChartData(
				fullStartTime,
				initialStartTime,
				5
			);
			return transformChartData(data);
		} catch (err) {
			console.error('Error loading historical chart data:', err);
			return null;
		}
	})();

	return {
		tenantless: false,
		initialChartData,
		streamed: {
			historicalChartData: historicalDataPromise,
		},
	};
};

/**
 * The tenants this subject belongs to, or an empty list if the list cannot be fetched.
 *
 * The bare list, not the overview: all that is wanted here is how many there are and what the
 * sole one is called, and the overview aggregates each tenant's latest glucose to answer that.
 * TenantsOverview fetches the aggregate itself once the page renders.
 *
 * A failure here must not block the dashboard: it only costs the single-tenant shortcut.
 */
async function getAccessibleTenants(apiClient: App.Locals['apiClient']) {
	try {
		return await apiClient.myTenants.getMyTenants();
	} catch (err) {
		console.error('Error loading the tenant list:', err);
		return [];
	}
}
