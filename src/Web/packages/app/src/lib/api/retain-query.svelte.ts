/**
 * Keeps a remote query that was constructed inside a `$derived` usable for as long as the
 * component lives.
 *
 * SvelteKit registers a query instance in its client-side query map from an `$effect.pre` created
 * wherever the query was constructed, and that registration is released by the effect's teardown.
 * Svelte runs the teardown of every effect a `$derived` owns the moment that derived loses its last
 * consumer (`remove_reaction` disconnects it and calls `freeze_derived_effects`), and re-reading it
 * does not unfreeze anything that would re-register: the entry is created in the query's
 * constructor, not in the effect body. So a query held as `$derived(cond ? someQuery() : null)`
 * leaves the map as soon as the markup reading it stops doing so — an optimistic override that
 * short-circuits the expression reading `.current`, or an `{#if}` block unmounting, is enough.
 * Reading the derived again does not bring it back: its own dependencies have not changed, so it
 * replays the cached, already-released instance. The next command's single-flight refresh then has
 * no instance to apply its payload to and is dropped silently — the command still resolves, so
 * nothing surfaces an error and the stale value survives until a full page load.
 *
 * Reading the derived from a component-owned effect gives it a consumer that outlives every
 * conditional reader, so the registration lasts as long as the component does.
 */
export function retainQuery(query: () => unknown): void {
  $effect(() => {
    query();
  });
}
