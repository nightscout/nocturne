<!--
  The custom report lab, a prototype: a report built from blocks and signals, a lens and groups in
  place of a form over the spec. It is not linked from anywhere; Save keeps the report.
-->
<script lang="ts">
  import { page } from "$app/state";
  import { getCustomReportCatalogue } from "#lib/api/generated/customReports.generated.remote.js";
  import StarterList from "#lib/components/reports/custom/workspace/StarterList.svelte";
  import ReportView from "#lib/components/reports/custom/workspace/ReportView.svelte";
  import type { Starter } from "#lib/components/reports/custom/workspace/workspace.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });

  /**
   * The chosen start: a starter, null for a blank chart, undefined while the
   * member chooses.
   */
  let start = $state<{ starter: Starter | null } | undefined>(undefined);
</script>

<div class="space-y-6">
  {#if page.data.limitTo24Hours === true}
    <p
      class="text-sm text-muted-foreground"
      role="status"
      data-testid="clamped-note"
    >
      Your access is limited to the last 24 hours. It cannot create reports.
    </p>
  {:else if catalogue.current}
    {#if start === undefined}
      <StarterList onstart={(starter) => (start = { starter })} />
    {:else}
      {#key start}
        <ReportView
          catalogue={catalogue.current}
          starter={start.starter}
          onrestart={() => (start = undefined)}
        />
      {/key}
    {/if}
  {/if}
</div>
