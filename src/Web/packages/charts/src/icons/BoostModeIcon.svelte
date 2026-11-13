<script lang="ts">
  import Zap from "@lucide/svelte/icons/zap";
  import type { IconProps } from "./types.js";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Boost mode - aggressive insulin delivery -->
<Zap class={className} {...rest} />
