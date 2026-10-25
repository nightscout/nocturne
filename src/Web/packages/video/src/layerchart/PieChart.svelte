<script>
  import { PieChart } from "layerchart";
  import Still from "./Still.svelte";
  let chart = $props();
</script>

<Still as={PieChart} {...chart} />
