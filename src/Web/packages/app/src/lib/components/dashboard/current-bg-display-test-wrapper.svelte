<script lang="ts">
  import * as Tooltip from "#lib/components/ui/tooltip/index.js";
  import CurrentBGDisplay from "./CurrentBGDisplay.svelte";

  interface Props {
    showPills?: boolean;
    layout?: "default" | "nightscout";
  }

  let { showPills = true, layout = "default" }: Props = $props();
</script>

<!-- The dashboard page is the container the mobile rail's breakpoint reads. -->
<Tooltip.Provider>
  <div class="@container">
    <CurrentBGDisplay {showPills} {layout} />
  </div>
</Tooltip.Provider>
