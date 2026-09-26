/** Span metadata as the API sends it: free-form, so each field is read by its type. */
type Metadata = Record<string, unknown> | null | undefined;

export function metadataString(metadata: Metadata, key: string): string | undefined {
	const value = metadata?.[key];
	return typeof value === 'string' ? value : undefined;
}

export function metadataNumber(metadata: Metadata, key: string): number | undefined {
	const value = metadata?.[key];
	return typeof value === 'number' ? value : undefined;
}

/** A temp basal's rate (U/h, or its `absolute` alias) and percent, or null when absent. */
export function tempBasalFields(metadata: Metadata): { rate: number | null; percent: number | null } {
	return {
		rate: metadataNumber(metadata, 'rate') ?? metadataNumber(metadata, 'absolute') ?? null,
		percent: metadataNumber(metadata, 'percent') ?? null,
	};
}
