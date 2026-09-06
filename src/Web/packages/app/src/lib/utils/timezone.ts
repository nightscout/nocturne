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
 * Offset of `timeZone` at an instant, in ms (positive east of UTC), derived via
 * formatToParts so no locale-dependent string parsing is involved.
 */
function tzOffsetMs(timeZone: string, at: Date): number {
	const parts = new Intl.DateTimeFormat('en-US', {
		timeZone,
		hourCycle: 'h23',
		year: 'numeric',
		month: '2-digit',
		day: '2-digit',
		hour: '2-digit',
		minute: '2-digit',
		second: '2-digit'
	}).formatToParts(at);
	const get = (type: string) => Number(parts.find((p) => p.type === type)?.value);
	return (
		Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'), get('second')) -
		at.getTime()
	);
}

/** Local calendar date (`YYYY-MM-DD`) of an instant in `timeZone`. */
function tzDateStr(timeZone: string, at: Date): string {
	return new Intl.DateTimeFormat('en-CA', { timeZone }).format(at);
}

/**
 * First instant of the local calendar day `dateStr` in `timeZone`, as a UTC
 * instant. Around a DST transition at midnight the naive guess
 * `utcMidnight - offsetAt(utcMidnight)` carries whichever offset the guess
 * itself lands on, so compute the two candidates and keep the earliest one
 * whose local calendar date actually equals `dateStr`.
 */
function localMidnightUtc(dateStr: string, timeZone: string): number {
	const utcMidnight = Date.parse(dateStr + 'T00:00:00Z');
	const c1 = utcMidnight - tzOffsetMs(timeZone, new Date(utcMidnight));
	const candidates = [c1, utcMidnight - tzOffsetMs(timeZone, new Date(c1))];
	const valid = candidates.filter((ms) => tzDateStr(timeZone, new Date(ms)) === dateStr);
	return valid.length > 0 ? Math.min(...valid) : c1;
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

	const startMs = localMidnightUtc(dateStr, timeZone);

	// End = last millisecond before the following local midnight, so 23- and
	// 25-hour days come out the right length instead of a fixed 24 hours.
	const [y, m, d] = dateStr.split('-').map(Number);
	const nextDateStr = new Date(Date.UTC(y, m - 1, d + 1)).toISOString().slice(0, 10);
	const end = new Date(localMidnightUtc(nextDateStr, timeZone) - 1);

	return { start: new Date(startMs), end };
}
