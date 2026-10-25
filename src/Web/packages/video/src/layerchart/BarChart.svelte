<script>
  import { BarChart } from "layerchart";
  import Still from "./Still.svelte";
  let chart = $props();
</script>

<Still as={BarChart} {...chart} />
