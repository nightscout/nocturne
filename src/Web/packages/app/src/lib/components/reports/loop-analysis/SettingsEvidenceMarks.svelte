<!--
  One setting's blocks on the slot axis: the profile's value as a step line across each block, and
  the observed value as a dot on its P25-P75 bar at the block's centre. A Low-confidence dot is
  hollow, on a faded dashed bar. The comparison is a diamond on a dashed bar beside it, on a ring of
  the background, hollow where it is Low.
  A block with no observed value draws only its profile.
-->
<script lang="ts">
  import { Circle, Line, Path, getChartContext } from "layerchart";
  import { SettingsEvidenceConfidence, type SettingsEvidenceFigure } from "#lib/api/index.js";
  import { blockSpan, matchingBlock } from "./settings-evidence-view";

  interface Props {
    blocks: SettingsEvidenceFigure[];
    comparison?: SettingsEvidenceFigure[] | null;
    color: string;
    /** A value as the axis shows it, in the viewer's units. */
    display?: (value: number) => number;
  }

  let { blocks, comparison = null, color, display = (v) => v }: Props = $props();

  const ctx = getChartContext();
  const xScale = $derived(ctx.xScale);
  const yScale = $derived(ctx.yScale);

  const centre = (figure: SettingsEvidenceFigure) => {
    const span = blockSpan(figure);
    return xScale((span.start + span.end) / 2);
  };
  const y = (value: number) => yScale(display(value));
  const LOW_BAR_OPACITY = 0.5;
  const diamond = (x: number, cy: number, r = 5) => `M${x},${cy - r}L${x + r},${cy}L${x},${cy + r}L${x - r},${cy}Z`;

  /** The profile's step line: a segment across each block, joined where adjacent blocks meet. */
  const steps = $derived.by(() => {
    const drawn = blocks
      .filter((b) => b.profile != null)
      .map((b) => ({ x1: xScale(blockSpan(b).start), x2: xScale(blockSpan(b).end), y: y(b.profile!) }));
    let path = "";
    drawn.forEach((s, i) => {
      const joined = i > 0 && Math.abs(drawn[i - 1].x2 - s.x1) < 0.5;
      path += `${joined ? "L" : "M"}${s.x1},${s.y}L${s.x2},${s.y}`;
    });
    return path;
  });

  // Beside the primary dot by a quarter of the narrowest block, so both stay inside their block.
  const offset = $derived(
    Math.min(...blocks.map((b) => (xScale(blockSpan(b).end) - xScale(blockSpan(b).start)) / 4), 12)
  );
</script>

{#if steps}
  <Path pathData={steps} fill="none" class="stroke-foreground/50" strokeWidth={2} data-testid="profile-step" />
{/if}

{#each blocks as block (`${block.startHour}`)}
  {#if block.observed != null}
    {@const x = centre(block)}
    {@const low = block.confidence === SettingsEvidenceConfidence.Low}
    {#if block.spread?.p25 != null && block.spread?.p75 != null}
      <Line
        x1={x}
        x2={x}
        y1={y(block.spread.p25)}
        y2={y(block.spread.p75)}
        stroke={color}
        strokeWidth={2}
        stroke-linecap={low ? "butt" : "round"}
        stroke-opacity={low ? LOW_BAR_OPACITY : 1}
        dashArray={low ? "3 2" : undefined}
        data-testid={low ? "spread-low" : "spread"}
      />
    {/if}
    <Circle
      cx={x}
      cy={y(block.observed)}
      r={4.5}
      fill={low ? "var(--background)" : color}
      stroke={color}
      strokeWidth={2}
      data-testid={low ? "observed-low" : "observed"}
    />
  {/if}
  {@const compared = matchingBlock(comparison, block)}
  {#if compared?.observed != null}
    {@const x = centre(block) + offset}
    {#if compared.spread?.p25 != null && compared.spread?.p75 != null}
      <Line x1={x} x2={x} y1={y(compared.spread.p25)} y2={y(compared.spread.p75)} stroke="var(--background)" strokeWidth={5} />
      <Line
        x1={x}
        x2={x}
        y1={y(compared.spread.p25)}
        y2={y(compared.spread.p75)}
        stroke="var(--foreground)"
        stroke-opacity="0.85"
        strokeWidth={1.5}
        dashArray="3 2"
        data-testid="compared-spread"
      />
    {/if}
    {@const d = diamond(x, y(compared.observed))}
    <Path pathData={d} fill="var(--background)" stroke="var(--background)" strokeWidth={4} />
    {@const comparedLow = compared.confidence === SettingsEvidenceConfidence.Low}
    <Path
      pathData={d}
      fill={comparedLow ? "var(--background)" : "var(--foreground)"}
      fillOpacity={comparedLow ? 1 : 0.85}
      stroke="var(--foreground)"
      strokeOpacity={0.85}
      strokeWidth={1.5}
      data-testid={comparedLow ? "observed-compared-low" : "observed-compared"}
    />
  {/if}
{/each}
