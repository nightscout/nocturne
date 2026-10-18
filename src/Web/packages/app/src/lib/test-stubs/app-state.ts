/**
 * Stub for $app/state in browser test environment.
 */
/** Route data. Declared wide so a test can stand in whatever its component reads. */
const data: Record<string, unknown> = {};

export const page = {
  url: new URL("http://localhost"),
  params: {},
  route: { id: "" },
  status: 200,
  error: null,
  data,
  form: null,
  /**
   * Written by the `$app/navigation` stub's `pushState` / `replaceState`, as SvelteKit does. Like
   * `url`, a plain property: a write does not re-run anything that read it.
   */
  state: {} as App.PageState,
};

export const navigating = null;

export const updated = {
  current: false,
  check: () => Promise.resolve(false),
};
