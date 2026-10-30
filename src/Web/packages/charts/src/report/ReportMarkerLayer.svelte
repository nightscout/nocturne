<!--
  The dashboard's bolus and carb markers at given plot positions, for a report's time series. The
  markers are the dashboard chart's own components; this sets the context they read their words from.
-->
<script lang="ts" module>
  import type { ReportMarkerKind } from "./types.js";

  export interface PlacedMarker {
    key: string;
    x: number;
    y: number;
    kind: ReportMarkerKind;
    amount: number;
    automatic: boolean;
  }
</script>

<script lang="ts">
  import BolusMarker from "../markers/BolusMarker.svelte";
  import CarbMarker from "../markers/CarbMarker.svelte";
  import { markerOnlyContext, setGlucoseChartContext } from "../context.svelte.js";
  import type { ChartFormat, GlucoseUnits } from "../format.js";
  import type { ChartLabels } from "../labels.js";

  interface Props {
    markers: PlacedMarker[];
    labels: ChartLabels;
    format: ChartFormat;
    glucoseUnits: GlucoseUnits;
  }

  let { markers, labels, format, glucoseUnits }: Props = $props();

  // The words are fixed for the life of the panel, as the dashboard's are for its chart.
  // svelte-ignore state_referenced_locally
  setGlucoseChartContext(markerOnlyContext(labels, format, glucoseUnits));

  const ignore = () => {};
</script>

{#each markers as marker (marker.key)}
  {#if marker.kind === "bolus"}
    <g data-testid="report-bolus-marker">
      <BolusMarker
        xPos={marker.x}
        yPos={marker.y}
        insulin={marker.amount}
        isOverride={false}
        bolusType={marker.automatic ? "AutomaticBolus" : "Bolus"}
        treatmentId={marker.key}
        onMarkerClick={ignore}
      />
    </g>
  {:else}
    <g data-testid="report-carb-marker">
      <CarbMarker
        xPos={marker.x}
        yPos={marker.y}
        carbs={marker.amount}
        label={null}
        treatmentId={marker.key}
        onMarkerClick={ignore}
      />
    </g>
  {/if}
{/each}
