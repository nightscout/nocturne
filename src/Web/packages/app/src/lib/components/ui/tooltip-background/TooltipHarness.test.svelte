<script lang="ts">
  import { Chart, Svg, Tooltip } from "layerchart";
</script>

<Chart width={400} height={300} tooltipContext={{ mode: "manual" }}>
  {#snippet children({ context })}
    <Svg>
      <rect
        role="presentation"
        data-testid="hit-area"
        width={context.width}
        height={context.height}
        fill="transparent"
        onpointermove={(e) => context.tooltip?.show(e, { value: 10 })}
      />
    </Svg>
    <Tooltip.Root {context}>
      {#snippet children({ data })}
        <Tooltip.Header value="Header" />
        <Tooltip.List>
          <Tooltip.Item label="Value" value={data.value} />
        </Tooltip.List>
      {/snippet}
    </Tooltip.Root>
  {/snippet}
</Chart>
