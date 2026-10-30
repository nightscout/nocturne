<!--
  Runs a spec for the viewer and hands the run to `children`. Its own component so the request
  is made only once the page has a spec to send: a saved report's once it is loaded, a link's
  once it is known to fit.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportRunRequest, CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  interface Props {
    request: CustomReportRunRequest;
    children: Snippet<[CustomReportRunResponse]>;
  }

  let { request, children }: Props = $props();

  const resource = contextResource(() => runCustomReport(request), {
    errorTitle: "Could not run the report",
  });
</script>

{#if resource.current}
  {@render children(resource.current)}
{/if}
