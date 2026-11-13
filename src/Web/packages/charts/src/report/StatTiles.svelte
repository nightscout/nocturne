<!-- A row of figures from a one-row report panel: one tile per field, label, value and unit. -->
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
  <dl class="grid grid-cols-[repeat(auto-fill,minmax(8rem,1fr))] gap-2" data-testid="report-stat-tiles">
    {#each panel.fields as field (field.name)}
      <div class="rounded-md border border-border p-2">
        <dt class="text-xs text-muted-foreground">{field.label}</dt>
        <dd class="text-lg font-semibold tabular-nums">
          {formatReportValue(row[field.name], field, format)}
          {#if field.unit && row[field.name] != null}
            <span class="text-xs font-normal text-muted-foreground">{field.unit}</span>
          {/if}
        </dd>
      </div>
    {/each}
  </dl>
{/if}
