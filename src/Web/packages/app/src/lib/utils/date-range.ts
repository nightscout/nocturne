/**
 * Utilities for parsing date range parameters from URLs
 */
import type { DateRangeInput } from '$lib/data/reports.remote';

/**
 * Parse date range input from URL search params.
 * Supports `from` & `to` query params, or a `days` param for relative ranges.
 *
 * @param url - The current page URL (from Svelte's `page.url`)
 * @param defaultDays - Default number of days to use if no params provided (default: 7)
 * @returns DateRangeInput parsed from URL, or a default value
 */
export function getDateRangeInputFromUrl(url: URL, defaultDays = 7): DateRangeInput {
	const daysParam = url.searchParams.get('days');
	const fromParam = url.searchParams.get('from');
	const toParam = url.searchParams.get('to');

	if (fromParam && toParam) {
		return { from: fromParam, to: toParam };
	} else if (daysParam) {
		const days = parseInt(daysParam);
		if (!isNaN(days)) return { days };
	}
	return { days: defaultDays };
}
