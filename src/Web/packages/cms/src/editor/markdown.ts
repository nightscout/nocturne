const PLAIN_SCALAR = /^[\p{L}\p{N}_][\p{L}\p{N}_ ./@+()'-]*$/u;
const RESERVED_SCALAR = /^(?:true|false|null|yes|no|on|off|y|n|~)$/i;
const LINE_SEPARATORS = /[\p{Zl}\p{Zp}]/gu;

function isNumeric(value: string): boolean {
	return !Number.isNaN(Number(value.replace(/_/g, '')));
}

function yamlScalar(value: string): string {
	const plain =
		PLAIN_SCALAR.test(value) &&
		!value.endsWith(' ') &&
		!RESERVED_SCALAR.test(value) &&
		!isNumeric(value);
	if (plain) return value;
	return JSON.stringify(value).replace(
		LINE_SEPARATORS,
		(c) => `\\u${c.charCodeAt(0).toString(16)}`
	);
}

function yamlValue(value: unknown): string {
	if (Array.isArray(value)) return `[${value.map((item) => yamlValue(item)).join(', ')}]`;
	if (typeof value === 'string') return yamlScalar(value);
	if (typeof value === 'object' && value !== null) return JSON.stringify(value);
	return String(value);
}

export function generateFrontmatter(metadata: Record<string, unknown>): string {
	const lines: string[] = ['---'];

	for (const [key, value] of Object.entries(metadata)) {
		if (value === undefined || value === null) continue;
		lines.push(`${yamlScalar(key)}: ${yamlValue(value)}`);
	}

	lines.push('---');
	return lines.join('\n');
}

export function toSvx(
	metadata: Record<string, unknown>,
	body: string,
	imports?: string[]
): string {
	const frontmatter = generateFrontmatter(metadata);
	const importBlock = imports?.length
		? `\n<script>\n${imports.map((i) => `  import ${i};`).join('\n')}\n</script>\n`
		: '';

	return `${frontmatter}${importBlock}\n${body}\n`;
}
