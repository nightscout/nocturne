<!--
  A custom report run: the report drawn by the charts package's ReportView, or the reasons its spec
  cannot run. Each panel that matches a built-in report links to it.
-->
<script lang="ts">
  import { ReportView } from "@nocturne/charts";
  import { readReport, type ReportPanel } from "@nocturne/charts/core";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
  import { chartFormat } from "#lib/utils/formatting.js";
  import { customReportLabels } from "./custom-report-labels.svelte.js";
  import SpecErrorList from "./SpecErrorList.svelte";

  interface Props {
    run: CustomReportRunResponse;
  }

  let { run }: Props = $props();

  const report = $derived(readReport(run.result));
  const errors = $derived(run.errors ?? []);

  /** The panel's built-in report as a path on this site: the API writes the link with its own view of the host. */
  function localPath(link: string): string | null {
    try {
      const url = new URL(link, "http://local");
      return url.pathname.startsWith("/reports/") ? `${url.pathname}${url.search}` : null;
    } catch {
      return null;
    }
  }
</script>

{#snippet panelLink(panel: ReportPanel)}
  {@const path = panel.link ? localPath(panel.link) : null}
  {#if path}
    <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- a path the API built from the report registry's own routes -->
    <a href={path} class="mt-1 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
      <ExternalLink class="size-3.5" aria-hidden="true" />
      Open the matching report
    </a>
  {/if}
{/snippet}

{#if errors.length > 0}
  <SpecErrorList {errors} spec={run.spec} />
{:else if report}
  <ReportView {report} format={chartFormat()} labels={customReportLabels()} headingLevel={2} controls={panelLink} />
{:else}
  <p class="text-sm text-muted-foreground" role="alert">The report data has a form that this page cannot show.</p>
{/if}
