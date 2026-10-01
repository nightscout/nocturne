export const isRecord = (value: unknown): value is Record<string, unknown> =>
	typeof value === 'object' && value !== null && !Array.isArray(value);

/** `value[key]` when `value` is an object holding a string there. */
export const stringField = (value: unknown, key: string): string | undefined => {
	if (!isRecord(value)) return undefined;
	const field = value[key];
	return typeof field === 'string' ? field : undefined;
};
