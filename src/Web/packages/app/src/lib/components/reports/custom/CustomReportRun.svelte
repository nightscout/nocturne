<!--
  A custom report run: the report drawn by the charts package's ReportView, or the reasons its spec
  cannot run. Each panel that matches a built-in report links to it, and each chart panel can be
  saved as a PNG.
-->
<script lang="ts">
  import { ReportView } from "@nocturne/charts";
  import { isHttpError } from "@sveltejs/kit";
  import { readReport, staticPanel, type ReportPanel } from "@nocturne/charts/core";
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
    /** Draw each chart's legend; a host that names the layers itself leaves it out. */
    legend?: boolean;
    /** Draw each panel's figures; a host that shows them itself leaves them out. */
    facts?: boolean;
    /** How the panels are laid out; `multiples` for one chart's small multiples. */
    layout?: "stack" | "multiples";
  }

  let { run, legend = true, facts = true, layout = "stack" }: Props = $props();

  const report = $derived(readReport(run.result));
  const errors = $derived(run.errors ?? []);

  let drawing = $state<string | null>(null);
  let failed = $state<{ panel: string; message: string } | null>(null);

  /** The panel's file name: its title in lower case, words joined by hyphens. */
  function fileName(panel: ReportPanel): string {
    const words = (panel.title ?? panel.dataset ?? "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    return `${words || panel.id}.png`;
  }

  /** What the page says for each refusal the image command gives (`ReportImageFailure`); anything else is the general sentence. */
  function failureText(err: unknown): string {
    const code = isHttpError(err) ? err.body.message : null;
    switch (code) {
      case "not_member":
        return "Only a member who is signed in can download an image.";
      case "rate_limited":
        return "You made many images in the last minute. Wait a minute and try again.";
      case "too_large":
        return "This panel has too much data for an image. Choose a shorter range.";
      case "busy":
        return "The image renderer is busy. Try again.";
      case "not_drawable":
        return "This panel cannot be made into an image.";
      default:
        return "The image could not be made.";
    }
  }

  async function download(panel: ReportPanel) {
    drawing = panel.id;
    failed = null;
    try {
      const base64 = await drawReportPanelImage({ spec: run.spec, panelId: panel.id });
      const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
      const url = URL.createObjectURL(new Blob([bytes], { type: "image/png" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName(panel);
      link.click();
      // A browser starts the download after the click returns; the URL must outlive it.
      setTimeout(() => URL.revokeObjectURL(url), 30_000);
    } catch (err) {
      failed = { panel: panel.id, message: failureText(err) };
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
  {#if path || (report && panel.status === "ok" && staticPanel(panel))}
    <div class="mt-1 flex flex-wrap items-center gap-3">
      {#if path}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- a path the API built from the report registry's own routes -->
        <a href={path} class="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
          <ExternalLink class="size-3.5" aria-hidden="true" />
          Open the matching report
        </a>
      {/if}
      {#if report && panel.status === "ok" && staticPanel(panel)}
        <Button
          variant="ghost-muted"
          size="xs"
          disabled={drawing !== null}
          onclick={() => download(panel)}
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
  <ReportView {report} format={chartFormat()} labels={customReportLabels()} headingLevel={2} controls={panelLink} {legend} {facts} {layout} />
{:else}
  <p class="text-sm text-muted-foreground" role="alert">The report data has a form that this page cannot show.</p>
{/if}
