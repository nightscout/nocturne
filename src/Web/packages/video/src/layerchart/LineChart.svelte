<script>
  import { LineChart } from "layerchart";
  import Still from "./Still.svelte";
  let chart = $props();
</script>

<Still as={LineChart} {...chart} />
