const DOCS_ORIGIN = "https://getnocturne.dev";

/**
 * A link to a page of the Nocturne docs, tagged so the docs' analytics count the visit as coming
 * from the app. `placement` names where in the app the link sits; it is sent as `utm_content`, so
 * it is a fixed string, never anything about the user or their data.
 */
export function docsUrl(path: `/docs${string}`, placement: string): string {
  const query = new URLSearchParams({
    utm_source: "nocturne-app",
    utm_medium: "app",
    utm_content: placement,
  });
  return `${DOCS_ORIGIN}${path}?${query}`;
}
