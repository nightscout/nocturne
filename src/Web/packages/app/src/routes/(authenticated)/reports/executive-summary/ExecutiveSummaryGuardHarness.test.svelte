<!-- The executive summary under the reports layout's ResourceGuard, fed by the real resource context. -->
<script lang="ts">
  import ResourceGuard from "#lib/components/reports/ResourceGuard.svelte";
  import { createResourceContext } from "#lib/hooks/resource-context.svelte.js";
  import ExecutiveSummary from "./+page.svelte";

  const resourceCtx = createResourceContext();
</script>

<ResourceGuard
  loading={resourceCtx.loading}
  error={resourceCtx.error}
  hasData={resourceCtx.hasData}
  refreshing={resourceCtx.refreshing}
  errorTitle={resourceCtx.errorTitle}
  onRetry={resourceCtx.refetch}
>
  <ExecutiveSummary />
</ResourceGuard>
