<!--
  A custom report run: the report drawn by the charts package's ReportView, or the reasons its spec
  cannot run. Each panel that matches a built-in report links to it, and each chart panel can be
  saved as the PNG an assistant gets from render_report_image.
-->
<script lang="ts">
  import { ReportView } from "@nocturne/charts";
  import { readReport, staticPrimitive, type ReportPanel, type ReportResult } from "@nocturne/charts/core";
  import Download from "@lucide/svelte/icons/download";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import { Button } from "#lib/components/ui/button/index.js";
  import { drawReportPanelImage } from "#lib/api/report-image.remote.js";
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

  let drawing = $state<string | null>(null);
  let failed = $state<{ panel: string; message: string } | null>(null);

  /** The panel's file name: its title in lower case, words joined by hyphens. */
  function fileName(panel: ReportPanel): string {
    const words = (panel.title ?? panel.dataset).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    return `${words || panel.id}.png`;
  }

  async function download(shown: ReportResult, panel: ReportPanel) {
    drawing = panel.id;
    failed = null;
    try {
      const base64 = await drawReportPanelImage({ result: { ...shown, panels: [panel] }, panelId: panel.id });
      const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
      const url = URL.createObjectURL(new Blob([bytes], { type: "image/png" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName(panel);
      link.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      failed = { panel: panel.id, message: err instanceof Error && err.message ? err.message : "The image could not be made." };
    } finally {
      drawing = null;
    }
  }

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
  {#if path || (report && panel.status === "ok" && staticPrimitive(panel))}
    <div class="mt-1 flex flex-wrap items-center gap-3">
      {#if path}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- a path the API built from the report registry's own routes -->
        <a href={path} class="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
          <ExternalLink class="size-3.5" aria-hidden="true" />
          Open the matching report
        </a>
      {/if}
      {#if report && panel.status === "ok" && staticPrimitive(panel)}
        {@const shown = report}
        <Button
          variant="ghost-muted"
          size="xs"
          disabled={drawing !== null}
          onclick={() => download(shown, panel)}
          data-testid="download-panel-png"
        >
          <Download class="size-3.5" aria-hidden="true" />
          {drawing === panel.id ? "Making the image" : "Download PNG"}
        </Button>
      {/if}
    </div>
    {#if failed?.panel === panel.id}
      <p class="mt-1 text-xs text-destructive" role="alert">{failed.message}</p>
    {/if}
  {/if}
{/snippet}

{#if errors.length > 0}
  <SpecErrorList {errors} spec={run.spec} />
{:else if report}
  <ReportView {report} format={chartFormat()} labels={customReportLabels()} headingLevel={2} controls={panelLink} />
{:else}
  <p class="text-sm text-muted-foreground" role="alert">The report data has a form that this page cannot show.</p>
{/if}
