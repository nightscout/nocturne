<script lang="ts">
  import AlertOctagon from "@lucide/svelte/icons/octagon-alert";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Hazard - important, requires attention -->
<AlertOctagon class={className} {...rest} />
