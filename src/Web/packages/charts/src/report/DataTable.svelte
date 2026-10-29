<!-- A report panel's rows as a table, one column per field, units in the headers. -->
<script lang="ts">
  import type { ChartFormat } from "../format.js";
  import { formatReportValue, type ReportPanel } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
  }

  let { panel, format, heightClass = "max-h-[280px]" }: Props = $props();
</script>

{#if panel.rows.length === 0}
  <p class="text-sm text-muted-foreground">No data in this range.</p>
{:else}
  <div class="{heightClass} overflow-auto rounded-md border border-border">
    <table class="w-full text-xs tabular-nums" data-testid="report-table">
      <thead class="sticky top-0 bg-background">
        <tr>
          {#each panel.fields as field (field.name)}
            <th scope="col" class="border-b border-border px-2 py-1 text-left font-medium">
              {field.label}{field.unit ? ` (${field.unit})` : ""}
            </th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each panel.rows as row, i (i)}
          <tr class="border-b border-border/50 last:border-0">
            {#each panel.fields as field (field.name)}
              <td class="px-2 py-1">{formatReportValue(row[field.name], field, format)}</td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}
