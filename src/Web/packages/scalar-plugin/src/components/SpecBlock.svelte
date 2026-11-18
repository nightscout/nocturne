<script lang="ts" generics="Row">
  import type { Snippet } from 'svelte';
  import { type Spec, documentsFrom, firstRows } from '../spec';

  let {
    rows,
    head,
    row,
  }: {
    /** The rows this block shows, read off one OpenAPI document. */
    rows: (spec: Spec, document: string) => Row[];
    head: Snippet;
    row: Snippet<[Row]>;
  } = $props();

  const loaded = firstRows(documentsFrom(location.hash), (spec, document) => rows(spec, document));
</script>

{#await loaded}
  <p class="status">Loading…</p>
{:then items}
  {#if items.length > 0}
    <table>
      <thead><tr>{@render head()}</tr></thead>
      <tbody>
        {#each items as item}
          <tr>{@render row(item)}</tr>
        {/each}
      </tbody>
    </table>
  {/if}
{:catch}
  <p class="status" role="status">This table could not be loaded.</p>
{/await}

<style>
  .status {
    margin: 1rem 0;
    font-size: var(--scalar-small, 0.9rem);
    color: var(--scalar-color-2);
  }
  table {
    width: 100%;
    margin: 1rem 0;
    border-collapse: collapse;
    font-size: var(--scalar-small, 0.9rem);
    color: var(--scalar-color-1);
  }
  table :global(th),
  table :global(td) {
    padding: 0.5rem 0.75rem;
    text-align: left;
    vertical-align: top;
    border-bottom: var(--scalar-border-width, 1px) solid var(--scalar-border-color);
  }
  table :global(th) { font-weight: 600; color: var(--scalar-color-2); }
  table :global(code) { font-family: var(--scalar-font-code, monospace); white-space: nowrap; }
  table :global(a) { color: inherit; text-decoration: underline; text-underline-offset: 3px; }
  table :global(.count) { text-align: right; color: var(--scalar-color-2); white-space: nowrap; }
</style>
