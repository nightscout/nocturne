import type { IconHints } from '../types';
import iconHints from '../../scripts/icon-hints.json';

/**
 * The library's built-in per-icon tuning, keyed by Lucide name. Two
 * systematic failures of the generic mapping are fixed here rather than by
 * hand-authoring: silhouettes Lucide draws as open paths never fill without a
 * `fill` index, and line icons carry more marks than a 48 px watercolour can
 * hold without a heavier `markRadius` and a lower `smallMarks` cap. Values
 * confirmed against `render_lucide`'s subpath listing (see
 * `scratchpad/reports/icon-hints.md`). One table lives in
 * `scripts/icon-hints.json`, read here by the browser and by the bake example
 * from disk, so both stay in step.
 */
export const ICON_HINTS: Readonly<Record<string, IconHints>> =
  iconHints as unknown as Readonly<Record<string, IconHints>>;