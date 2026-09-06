<script lang="ts">
  import { contextResource } from "./resource-context.svelte";
  import type { ReportsParamsReturn } from "./date-params.svelte";

  interface QueryLike {
    loading: boolean;
    error: unknown;
    current: string | undefined;
    refresh: () => void;
  }

  let {
    query,
    dateParams,
  }: { query: QueryLike; dateParams: ReportsParamsReturn } = $props();

  // Mirrors a report page: one resource, asked for with `dateParams` so it also
  // exposes `date`. Both props are read once here, exactly as a page reads its
  // params object once — the test drives the values through their getters.
  const resource = contextResource(() => query, {
    errorTitle: "Error Loading Test Report",
    dateParams,
  });
</script>

<p data-testid="current">{resource.current ?? "none"}</p>
<p data-testid="loading">{String(resource.loading)}</p>
<p data-testid="day-count">{resource.date.dayCount}</p>
