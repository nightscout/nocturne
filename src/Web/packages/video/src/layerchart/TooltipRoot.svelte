<script>
  import { Tooltip } from "layerchart";
  let tooltip = $props();
</script>

<!-- Unportaled, so the tooltip stays inside its chart and the composition can scope it. -->
<Tooltip.Root {...tooltip} motion="none" fadeDuration={0} portal={false} />
