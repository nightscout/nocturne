<script lang="ts">
  import AlertOctagon from "lucide-svelte/icons/alert-octagon";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Hazard - important, requires attention -->
<AlertOctagon class={className} {...rest} />
