<!--
  The figures of a one-row report panel as a Figure Strip (DESIGN.md): one hairline frame whose cells,
  one per field, are divided by 1px rules, each a muted label over the figure and its unit. A frame,
  not a card: in the workspace the panel already sits in one.
-->
<script lang="ts">
  import type { ChartFormat } from "../format.js";
  import { formatReportValue, type ReportPanel } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    /** Shown in place of the rows when there are none: the host's words. */
    emptyText?: string;
  }

  let { panel, format, emptyText = "No data in this range." }: Props = $props();

  const row = $derived(panel.rows[0]);
</script>

{#if !row}
  <p class="text-sm text-muted-foreground">{emptyText}</p>
{:else}
  <div class="overflow-hidden rounded-xl border border-border">
    <!-- Each cell rules its right and bottom edge; the list is 1px wider and taller than the frame, which
         clips the outer rules, so a part-filled last row leaves no block of rule colour. -->
    <dl class="m-0 -mr-px -mb-px grid grid-cols-[repeat(auto-fill,minmax(9rem,1fr))]" data-testid="report-stat-tiles">
      {#each panel.fields as field (field.name)}
        <div class="border-r border-b border-border px-4 py-3">
          <dt class="text-xs text-muted-foreground">{field.label}</dt>
          <dd class="m-0 mt-1 flex flex-wrap items-baseline gap-x-1">
            <span class="text-xl font-semibold whitespace-nowrap tabular-nums">{formatReportValue(row[field.name], field, format)}</span>
            {#if field.unit && row[field.name] != null}
              <span class="text-xs text-muted-foreground">{field.unit}</span>
            {/if}
          </dd>
        </div>
      {/each}
    </dl>
  </div>
{/if}
