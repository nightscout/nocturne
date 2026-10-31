<!--
  A report panel's rows as a table, one column per field, units in the headers. With `onselect`,
  a row can be picked by click or by Enter or Space on it; `selected` marks the picked one.
-->
<script lang="ts">
  import { Key } from "@nocturne/ui/keys";
  import type { ChartFormat } from "../format.js";
  import { formatReportValue, type ReportPanel, type ReportRow } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
    /** Called with a row and its index when the reader picks it; rows are not selectable without it. */
    onselect?: (row: ReportRow, index: number) => void;
    /** The index of the picked row. */
    selected?: number | null;
    /** Shown in place of the rows when there are none: the host's words. */
    emptyText?: string;
  }

  let { panel, format, heightClass = "max-h-[280px]", onselect, selected = null, emptyText = "No data in this range." }: Props = $props();

  function onkeydown(event: KeyboardEvent, row: ReportRow, index: number) {
    if (event.key !== Key.Enter && event.key !== Key.Space) return;
    event.preventDefault();
    onselect?.(row, index);
  }
</script>

{#if panel.rows.length === 0}
  <p class="text-sm text-muted-foreground">{emptyText}</p>
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
          {#if onselect}
            <tr
              class="cursor-pointer border-b border-border/50 last:border-0 hover:bg-muted/50 focus-visible:outline-2 focus-visible:outline-ring aria-selected:bg-muted"
              tabindex="0"
              aria-selected={selected === i}
              onclick={() => onselect(row, i)}
              onkeydown={(event) => onkeydown(event, row, i)}
            >
              {#each panel.fields as field (field.name)}
                <td class="px-2 py-1">{formatReportValue(row[field.name], field, format)}</td>
              {/each}
            </tr>
          {:else}
            <tr class="border-b border-border/50 last:border-0">
              {#each panel.fields as field (field.name)}
                <td class="px-2 py-1">{formatReportValue(row[field.name], field, format)}</td>
              {/each}
            </tr>
          {/if}
        {/each}
      </tbody>
    </table>
  </div>
{/if}
