/**
 * Stands in for `$lib/utils` in the rendering tests.
 *
 * The real barrel re-exports formatting, which reads the appearance store,
 * which names the widget ids from the generated API client — a .NET build
 * artifact the job running these tests does not build. Only `cn` is on the
 * path under test, and class names are not what it asserts.
 */
export function cn(...classes: unknown[]): string {
	return classes.filter((c) => typeof c === "string" && c.length > 0).join(" ");
}

export type WithElementRef<T, U = HTMLElement> = T & { ref?: U | null };
