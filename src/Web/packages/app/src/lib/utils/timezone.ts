/**
 * First instant of a `YYYY-MM-DD` date in the browser's local time.
 *
 * `new Date("2026-03-29")` is parsed as UTC midnight while
 * `new Date("2026-03-29T00:00:00")` is parsed as local midnight, so a range
 * built from a bare date string and a time-bearing one is skewed by the
 * viewer's offset.
 */
export function localDayStart(dateStr: string): Date {
	return new Date(dateStr + 'T00:00:00');
}

/**
 * Last instant of a `YYYY-MM-DD` date in the browser's local time. Derived from
 * the following local midnight rather than a fixed 24 hours, so it holds across
 * the 23- and 25-hour days either side of a DST change.
 */
export function localDayEnd(dateStr: string): Date {
	const start = localDayStart(dateStr);
	const nextDay = new Date(start);
	nextDay.setDate(nextDay.getDate() + 1);
	return new Date(nextDay.getTime() - 1);
}

/**
 * Compute UTC start/end of a local day for a given IANA timezone.
 * Falls back to UTC if timezone is not provided.
 */
export function getLocalDayBoundariesUtc(
	dateStr: string,
	timeZone?: string | null
): { start: Date; end: Date } {
	if (!timeZone) {
		const start = new Date(dateStr + 'T00:00:00Z');
		const end = new Date(dateStr + 'T23:59:59.999Z');
		return { start, end };
	}

	// Use Intl to determine the UTC offset at local midnight for the given timezone
	const utcMidnight = new Date(dateStr + 'T00:00:00Z');
	const utcStr = utcMidnight.toLocaleString('en-US', { timeZone: 'UTC' });
	const localStr = utcMidnight.toLocaleString('en-US', { timeZone });
	const offsetMs = new Date(localStr).getTime() - new Date(utcStr).getTime();

	// Local midnight = UTC midnight minus the timezone offset
	const start = new Date(utcMidnight.getTime() - offsetMs);
	const end = new Date(start.getTime() + 24 * 60 * 60 * 1000 - 1);
	return { start, end };
}
