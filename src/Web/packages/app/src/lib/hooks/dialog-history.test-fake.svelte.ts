/**
 * A reactive stand-in for `$app/state` and the shallow-routing half of `$app/navigation`. Like
 * SvelteKit, `pushState` applies its URL and state a task after it is called, so a hook can run
 * between the push and the state it pushed; `back` pops the stack the same way.
 */

interface Entry {
  url: URL;
  state: Record<string, unknown>;
}

// eslint-disable-next-line svelte/prefer-svelte-reactivity -- replaced whole, never mutated, as SvelteKit replaces page.url
export const page = $state<Entry>({ url: new URL("http://localhost/"), state: {} });

const stack: Entry[] = [];

function resolve(url: string | URL): URL {
  return url === "" ? page.url : new URL(url, page.url);
}

export function reset(url = "http://localhost/"): void {
  stack.length = 0;
  // eslint-disable-next-line svelte/prefer-svelte-reactivity -- replaced whole, never mutated, as SvelteKit replaces page.url
  page.url = new URL(url);
  page.state = {};
}

export function pushState(url: string | URL, state: Record<string, unknown>): void {
  stack.push({ url: page.url, state: page.state });
  const next = resolve(url);
  setTimeout(() => {
    page.url = next;
    page.state = state;
  });
}

export function replaceState(url: string | URL, state: Record<string, unknown>): void {
  page.url = resolve(url);
  page.state = state;
}

/** What `history.back()` does to SvelteKit's page: the previous entry, a task later. */
export function back(): void {
  setTimeout(() => {
    const previous = stack.pop();
    if (!previous) return;
    page.url = previous.url;
    page.state = previous.state;
  });
}
