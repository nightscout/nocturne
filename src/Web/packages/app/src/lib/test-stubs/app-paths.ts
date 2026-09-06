/**
 * `$app/paths` for the browser test environment. The standalone vitest config
 * has only the Svelte plugin, not SvelteKit's, so this module is aliased in to
 * stand in for the framework's. `resolve` echoes the route id it is given,
 * which is what the app deployed at the root would resolve to.
 */
export const base = "";
export const assets = "";

export function resolve(pathname: string): string {
  return pathname;
}

export function resolveRoute(
  id: string,
  params?: Record<string, string>
): string {
  if (!params) return id;
  return Object.entries(params).reduce(
    (acc, [key, value]) => acc.replace(`[${key}]`, value),
    id
  );
}
