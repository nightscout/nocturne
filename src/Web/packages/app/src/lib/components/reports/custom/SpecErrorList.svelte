<!--
  Why a report spec cannot run: each error as the API writes it, then, folded, the part of the spec
  each one names and the spec as it was sent, shown as text.
-->
<script lang="ts">
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import type { CustomReportSpecError } from "@nocturne/api-client";

  interface Props {
    errors: CustomReportSpecError[];
    /** The spec as it was sent; not shown when the link could not be read. */
    spec?: unknown;
  }

  let { errors, spec }: Props = $props();

  const specText = $derived(spec === undefined || spec === null ? null : JSON.stringify(spec, null, 2));
  const paths = $derived(errors.map((e) => e.path).filter((p): p is string => !!p));
</script>

<div class="space-y-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert">
  <div class="flex items-center gap-2 font-medium">
    <TriangleAlert class="size-4 text-destructive" aria-hidden="true" />
    This report cannot run.
  </div>
  <ul class="list-disc space-y-1 pl-6 text-sm" data-testid="spec-errors">
    {#each errors as error, i (i)}
      <li>{error.message}</li>
    {/each}
  </ul>
  {#if paths.length > 0 || specText}
    <details>
      <summary class="cursor-pointer text-sm text-muted-foreground">Show the technical details</summary>
      {#if paths.length > 0}
        <ul class="mt-2 space-y-1 text-xs" data-testid="spec-error-paths">
          {#each paths as path, i (i)}
            <li><code class="rounded bg-muted px-1 break-all">{path}</code></li>
          {/each}
        </ul>
      {/if}
      {#if specText}
        <pre class="mt-2 max-h-80 overflow-auto rounded bg-muted p-2 text-xs">{specText}</pre>
      {/if}
    </details>
  {/if}
</div>
