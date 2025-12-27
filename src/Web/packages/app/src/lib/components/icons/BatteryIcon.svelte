<script lang="ts">
  import Battery from "lucide-svelte/icons/battery";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Battery icon - pump battery change -->
<Battery class={className} {...rest} />
