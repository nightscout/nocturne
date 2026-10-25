/**
 * `$app/state` for a film. Unlike the browser tests' stub, `page` is reactive, so a page that keeps
 * its state in the URL (a toggle, the day shown, the date range) re-renders when a film's action
 * changes it through `$app/navigation`.
 */
export const page = $state({
  // Replaced whole on every navigation, as SvelteKit's own `page.url` is, never changed in place.
  // eslint-disable-next-line svelte/prefer-svelte-reactivity
  url: new URL("http://localhost"),
  params: {},
  route: { id: "" },
  status: 200,
  error: null,
  data: {},
  form: null,
  state: {},
});

export const navigating = { current: null };

export const updated = {
  current: false,
  check: () => Promise.resolve(false),
};
