<script lang="ts">
  import type { ComponentProps } from "svelte";
  import GlucoseSparkline from "./GlucoseSparkline.svelte";

  let sparklineProps: ComponentProps<typeof GlucoseSparkline> = $props();
</script>

<div class="h-16 w-50">
  <GlucoseSparkline {...sparklineProps} />
</div>
