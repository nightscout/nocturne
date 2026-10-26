/**
 * `$app/paths` for the test environments, as the app deployed at the root resolves: a route id
 * (leading `/`) has its params filled in, and a path (no leading `/`) gains one. The standalone
 * vitest configs have only the Svelte plugin, not SvelteKit's, so this module stands in for the
 * framework's.
 */
export function resolve(id: string, params?: Record<string, string>): string {
  if (!id.startsWith("/")) return `/${id}`;
  if (!params) return id;
  return Object.entries(params).reduce((acc, [key, value]) => acc.replace(`[${key}]`, value), id);
}

export function asset(file: string): string {
  return `/${file}`;
}
