<!--
  A markers layer: the dashboard's bolus and carb glyphs (a long-acting injection as a bolus in the
  basal colour, a BG check as a ring) at their amounts, on the track's first line where it has one,
  with each amount written beside it. Whether a bolus was the algorithm's (drawn outlined) is the
  API's. No glyph is drawn without the chart's words.
-->
<script lang="ts">
  import { Circle, Path, Text } from "layerchart";
  import type { ChartLabels } from "../../labels.js";
  import { markerPath } from "../static/static-marks.js";
  import { placeGlyphs, type TrackBand } from "../layered/layered-layout.js";
  import type { ReportLayer, ReportTrack } from "../types.js";
  import type { MarkText } from "./mark-text.js";

  interface Props {
    layer: ReportLayer;
    track: ReportTrack;
    band: TrackBand;
    x: (raw: number) => number;
    yOf: (layer: ReportLayer) => ((value: number) => number) | null;
    paint: (css: string) => string;
    labels?: ChartLabels;
    text: MarkText;
  }

  let { layer, track, band, x, yOf, paint, labels, text }: Props = $props();

  const glyphs = $derived(placeGlyphs(layer, track, band, x, yOf));

  const COLORS = {
    bolus: "var(--insulin-bolus)",
    carbs: "var(--carbs)",
    basal_injection: "var(--insulin-basal)",
    bg_check: "var(--foreground)",
  } as const;

  function amountText(kind: keyof typeof COLORS, amount: number, words: ChartLabels): string {
    if (kind === "carbs") return words.carbAmount(String(Math.round(amount)));
    if (kind === "bg_check") return String(amount);
    return words.insulinAmount(amount.toFixed(1));
  }
</script>

{#if labels}
  {#each glyphs as glyph (glyph.key)}
    {@const color = paint(COLORS[glyph.kind])}
    {#if glyph.kind === "bg_check"}
      <Circle cx={glyph.x} cy={glyph.y} r={4} fill="none" stroke={color} strokeWidth={1.5} data-testid="report-marker" />
    {:else}
      <Path
        pathData={markerPath(glyph.kind === "carbs" ? "carbs" : "bolus", glyph.x, glyph.y)}
        fill={glyph.automatic ? "none" : color}
        stroke={glyph.automatic ? color : "none"}
        strokeWidth={1.5}
        opacity={0.9}
        data-testid="report-marker"
      />
    {/if}
    <Text
      x={glyph.x}
      y={glyph.kind === "carbs" ? glyph.y - 11 : glyph.kind === "bg_check" ? glyph.y - 8 : glyph.y + 20}
      value={amountText(glyph.kind, glyph.amount, labels)}
      textAnchor="middle"
      fontSize={10}
      {...text.strong}
      fill={color}
    />
  {/each}
{/if}
