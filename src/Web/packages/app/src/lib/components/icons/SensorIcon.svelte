<script lang="ts">
  import Radio from "lucide-svelte/icons/radio";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Sensor icon - CGM sensor events (start, change, stop) -->
<Radio class={className} {...rest} />
