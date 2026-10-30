<!--
  Why a report spec cannot run: each error with the panel and field it names, then the spec as it
  was sent, folded. The API writes the messages; the spec is shown as text.
-->
<script lang="ts">
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import type { CustomReportSpecError } from "#lib/api/generated/nocturne-api-client.js";

  interface Props {
    errors: CustomReportSpecError[];
    /** The spec as it was sent; not shown when the link could not be read. */
    spec?: unknown;
  }

  let { errors, spec }: Props = $props();

  const specText = $derived(spec === undefined || spec === null ? null : JSON.stringify(spec, null, 2));
</script>

<div class="space-y-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert">
  <div class="flex items-center gap-2 font-medium">
    <TriangleAlert class="size-4 text-destructive" aria-hidden="true" />
    This report cannot run. Nocturne found these problems in its spec:
  </div>
  <ul class="list-disc space-y-1 pl-6 text-sm" data-testid="spec-errors">
    {#each errors as error, i (i)}
      <li>
        <code class="rounded bg-muted px-1 text-xs">{error.path}</code>
        <span>{error.message}</span>
      </li>
    {/each}
  </ul>
  {#if specText}
    <details>
      <summary class="cursor-pointer text-sm text-muted-foreground">Show the spec</summary>
      <pre class="mt-2 max-h-80 overflow-auto rounded bg-muted p-2 text-xs">{specText}</pre>
    </details>
  {/if}
</div>
