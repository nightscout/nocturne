<!--
  A spans layer: a lane for each value of its category (pump mode, profile, override ...), named at
  the left, and each span from its start to its end in its colour, named where it is wide enough.
-->
<script lang="ts">
  import { Rect, Text } from "layerchart";
  import { spanRects, type TrackBand } from "../layered/layered-layout.js";
  import type { ReportLayer } from "../types.js";
  import type { MarkText } from "./mark-text.js";

  interface Props {
    layer: ReportLayer;
    band: TrackBand;
    x: (raw: number) => number;
    paint: (css: string) => string;
    text: MarkText;
    /** Where the lanes' names start, in the plot's coordinates: in its left margin. */
    laneX: number;
  }

  let { layer, band, x, paint, text, laneX }: Props = $props();

  const layout = $derived(spanRects(layer, band, x));
  /** The width a span's name needs, at 10px, before it is written inside it. */
  const fits = (width: number, label: string) => width > label.length * 6 + 8;
</script>

{#each layout.lanes as lane (lane.name)}
  <Text x={laneX} y={lane.y + lane.height / 2 + 3} value={lane.name} fontSize={10} {...text.muted} />
{/each}
{#each layout.spans as span (span.key)}
  <Rect x={span.x} y={span.y} width={span.width} height={span.height} fill={paint(span.color)} fillOpacity={0.55} data-testid="report-layer-span" />
  {#if fits(span.width, span.label) && span.height >= 10}
    <Text x={span.x + 4} y={span.y + span.height / 2 + 3} value={span.label} fontSize={10} {...text.strong} />
  {/if}
{/each}
