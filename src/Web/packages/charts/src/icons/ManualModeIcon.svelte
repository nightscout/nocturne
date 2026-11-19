<script lang="ts">
  import Hand from "@lucide/svelte/icons/hand";
  import type { IconProps } from "./types.js";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Manual/Open-loop mode - no automation -->
<Hand class={className} {...rest} />
