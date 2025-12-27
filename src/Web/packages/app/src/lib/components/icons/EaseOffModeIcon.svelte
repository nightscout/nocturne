<script lang="ts">
  import TrendingDown from "lucide-svelte/icons/trending-down";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Ease-off mode - reduced insulin delivery -->
<TrendingDown class={className} {...rest} />
