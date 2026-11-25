<script lang="ts">
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import type { IconProps } from "./types.js";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Limited mode - partial automation (e.g., Op5 Limited) -->
<ShieldAlert class={className} {...rest} />
