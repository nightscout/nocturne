/**
 * `$app/paths` for the test environments, as the app deployed at the root resolves: a route id
 * (leading `/`) loses its groups and has its params filled in, and a path (no leading `/`) gains one. The standalone
 * vitest configs have only the Svelte plugin, not SvelteKit's, so this module stands in for the
 * framework's.
 */
export function resolve(id: string, params?: Record<string, string>): string {
  if (!id.startsWith("/")) return `/${id}`;
  const path = id.replace(/\/\([^/]+\)/g, "") || "/";
  if (!params) return path;
  return Object.entries(params).reduce<string>((acc, [key, value]) => acc.replace(`[${key}]`, value), path);
}

/** Every path is a page of the app here; a test that needs otherwise mocks `$app/paths`. */
export async function match(url: URL | string): Promise<{ id: string; params: Record<string, string> }> {
  return { id: new URL(url, "http://localhost").pathname, params: {} };
}

export function asset(file: string): string {
  return `/${file}`;
}
