/**
 * JavaScript reserved keywords that cannot be used as export names.
 * Maps known API method names to safe alternatives.
 */
const RESERVED_KEYWORD_MAP: Record<string, string> = {
  delete: 'remove',
};

const JS_RESERVED_KEYWORDS = new Set([
  'delete', 'new', 'return', 'switch', 'throw', 'try', 'catch', 'finally',
  'class', 'const', 'let', 'var', 'function', 'import', 'export', 'default',
  'void', 'typeof', 'instanceof', 'in', 'of', 'if', 'else', 'for', 'while',
  'do', 'break', 'continue', 'with', 'yield', 'await', 'async', 'super',
  'this', 'null', 'undefined', 'true', 'false', 'enum', 'implements',
  'interface', 'package', 'private', 'protected', 'public', 'static',
]);

/**
 * Ensure a function name is not a JavaScript reserved keyword.
 * Uses explicit mapping for common cases (delete -> remove),
 * falls back to underscore prefix for others.
 */
function safeExportName(name: string): string {
  if (!JS_RESERVED_KEYWORDS.has(name)) return name;
  return RESERVED_KEYWORD_MAP[name] ?? `_${name}`;
}

/**
 * Convert operationId to function name.
 * "Note_GetNotes" -> "getNotes"
 * "Note_CreateNote" -> "createNote"
 * "ClockFaces_Delete" -> "remove" (reserved keyword handling)
 */
export function operationIdToFunctionName(operationId: string): string {
  const parts = operationId.split('_');
  const name = parts.length > 1 ? parts.slice(1).join('_') : parts[0];
  const functionName = name.charAt(0).toLowerCase() + name.slice(1);
  return safeExportName(functionName);
}

/**
 * Pluralize a single word.
 */
function pluralize(word: string): string {
  const lower = word.toLowerCase();
  if (lower.endsWith('s')) return lower;
  if (lower.endsWith('y') && !/[aeiou]y$/.test(lower)) {
    return lower.slice(0, -1) + 'ies';
  }
  if (/(?:sh|ch|x|z)$/.test(lower)) {
    return lower + 'es';
  }
  return lower + 's';
}

/**
 * Convert tag to camelCase file name with proper pluralization.
 * "Note" -> "notes"
 * "V4 Notes" -> "notes"
 * "V4 Compression Lows" -> "compressionLows"
 * "V1 Connector Status" -> "connectorStatus"
 * "Battery" -> "batteries"
 */
export function tagToFileName(tag: string): string {
  const cleaned = tag.replace(/^V\d+\s*/i, '').trim();
  const words = cleaned.split(/\s+/);

  // Pluralize the last word
  const lastWord = pluralize(words[words.length - 1]);
  const prefix = words.slice(0, -1);

  // Build camelCase: first word lowercase, rest capitalized
  const parts = [...prefix.map(w => w.toLowerCase()), lastWord];
  return parts[0] + parts.slice(1).map(w => w.charAt(0).toUpperCase() + w.slice(1)).join('');
}

/**
 * Resolve invalidation targets.
 * Short names resolve within same tag, full operationIds are used as-is.
 * "GetNotes" with currentTag "Note" -> "getNotes"
 * "Trackers_GetActiveInstances" -> "getActiveInstances" (from trackers)
 */
export function resolveInvalidation(
  invalidate: string,
  currentTag: string
): { functionName: string; fromTag: string } {
  if (invalidate.includes('_')) {
    const [tag, ...rest] = invalidate.split('_');
    return {
      functionName: operationIdToFunctionName(invalidate),
      fromTag: tag,
    };
  }

  return {
    functionName: invalidate.charAt(0).toLowerCase() + invalidate.slice(1),
    fromTag: currentTag,
  };
}
