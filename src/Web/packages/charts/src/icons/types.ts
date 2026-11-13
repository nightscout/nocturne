/**
 * Shared props interface for icon wrapper components.
 * Used to properly type lucide-svelte icon wrappers in Svelte 5.
 */
export interface IconProps {
  class?: string;
  size?: number;
  strokeWidth?: number;
  color?: string;
  [key: string]: unknown;
}
