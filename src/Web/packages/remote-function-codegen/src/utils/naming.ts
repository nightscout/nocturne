/**
 * Convert operationId to function name.
 * "Note_GetNotes" -> "getNotes"
 * "Note_CreateNote" -> "createNote"
 */
export function operationIdToFunctionName(operationId: string): string {
  const parts = operationId.split('_');
  const name = parts.length > 1 ? parts.slice(1).join('_') : parts[0];
  return name.charAt(0).toLowerCase() + name.slice(1);
}

/**
 * Convert tag to file name.
 * "Note" -> "notes"
 * "V4 Notes" -> "notes"
 * "Trackers" -> "trackers"
 */
export function tagToFileName(tag: string): string {
  const cleaned = tag.replace(/^V\d+\s*/i, '');
  const lower = cleaned.toLowerCase();
  if (lower.endsWith('s')) return lower;
  return lower + 's';
}

/**
 * Convert tag to apiClient property name.
 * "Note" -> "notes"
 * "V4 Notes" -> "notes"
 * "Trackers" -> "trackers"
 */
export function tagToClientProperty(tag: string): string {
  return tagToFileName(tag);
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
