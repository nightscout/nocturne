<script lang="ts">
  import { Circle, Line, getChartContext } from "layerchart";
  import { AlertReplayEventKind, type AlertReplayEvent } from "#lib/api/generated/nocturne-api-client.js";
  import { severityVar } from "./severity";

  interface Marker {
    ev: AlertReplayEvent;
    tMs: number;
  }

  interface Props {
    firedMarkers: Marker[];
    currentDate: Date | null;
  }

  let { firedMarkers, currentDate }: Props = $props();

  const chartCtx = getChartContext();
  const xScale = $derived(chartCtx.xScale);
  const height = $derived(chartCtx.height);
</script>

{#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
  {@const px = xScale(new Date(m.tMs))}
  {@const color = severityVar(m.ev.severity)}
  {@const isResolved = m.ev.kind === AlertReplayEventKind.AutoResolved}
  {@const isSuppressed = m.ev.kind === AlertReplayEventKind.SuppressedByDnd}
  <Line
    x1={px}
    x2={px}
    y1={height - 20}
    y2={height - 8}
    stroke={color}
    strokeWidth={1.5}
    dashArray={isSuppressed ? "2 2" : undefined}
    opacity={isSuppressed ? 0.6 : 1}
  />
  <Circle
    cx={px}
    cy={height - 8}
    r={4}
    fill={isResolved || isSuppressed ? "none" : color}
    stroke={color}
    strokeWidth={isResolved || isSuppressed ? 1.5 : 0}
    opacity={isSuppressed ? 0.6 : 1}
  />
{/each}
{#if currentDate}
  {@const px = xScale(currentDate)}
  <Line
    x1={px}
    x2={px}
    y1={0}
    y2={height}
    class="stroke-foreground/80"
    strokeWidth={1.5}
  />
{/if}
