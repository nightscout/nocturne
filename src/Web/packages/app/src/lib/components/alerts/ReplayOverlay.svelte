<script lang="ts">
  import { getChartContext } from "layerchart";
  import { AlertReplayEventKind } from "$api-clients";
  import { severityVar } from "./severity";

  interface Marker {
    ev: {
      ruleId?: string | null;
      severity?: number | null;
      kind?: string | null;
    };
    tMs: number;
  }

  interface Props {
    firedMarkers: Marker[];
    currentDate: Date | null;
  }

  let { firedMarkers, currentDate }: Props = $props();

  const chartCtx = getChartContext();
  const xScale = $derived(chartCtx.xScale as unknown as (d: Date) => number);
  const height = $derived(chartCtx.height);
</script>

{#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
  {@const px = xScale(new Date(m.tMs))}
  {@const color = severityVar(m.ev.severity)}
  {@const isResolved = m.ev.kind === AlertReplayEventKind.AutoResolved}
  {@const isSuppressed = m.ev.kind === AlertReplayEventKind.SuppressedByDnd}
  <line
    x1={px}
    x2={px}
    y1={height - 20}
    y2={height - 8}
    stroke={color}
    stroke-width="1.5"
    stroke-dasharray={isSuppressed ? "2 2" : null}
    opacity={isSuppressed ? 0.6 : 1}
  />
  <circle
    cx={px}
    cy={height - 8}
    r="4"
    fill={isResolved || isSuppressed ? "none" : color}
    stroke={color}
    stroke-width={isResolved || isSuppressed ? 1.5 : 0}
    opacity={isSuppressed ? 0.6 : 1}
  />
{/each}
{#if currentDate}
  {@const px = xScale(currentDate)}
  <line
    x1={px}
    x2={px}
    y1="0"
    y2={height}
    class="stroke-foreground/80"
    stroke-width="1.5"
  />
{/if}
