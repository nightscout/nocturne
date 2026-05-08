<script lang="ts">
  import { ChartClipPath, Highlight } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  const ctx = getGlucoseChartContext();
</script>

<ChartClipPath>
  <Highlight
    x={(d) => d.time}
    y={(d) => ctx.layout.glucose.scale(d.sgv)}
    points
    lines
    onPointClick={ctx.inspection
      ? (_e, { data }) =>
          ctx.inspection!.inspect(
            data.time,
            { sgv: data.sgv, color: data.color },
            data.dataSource
          )
      : undefined}
  />
</ChartClipPath>
