<script>
  import { AreaChart } from "layerchart";
  import Still from "./Still.svelte";
  let chart = $props();
</script>

<Still as={AreaChart} {...chart} />
