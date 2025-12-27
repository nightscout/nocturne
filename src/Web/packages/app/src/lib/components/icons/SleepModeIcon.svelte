<script lang="ts">
  import Moon from "lucide-svelte/icons/moon";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Sleep mode - nighttime/sleep automation -->
<Moon class={className} {...rest} />
