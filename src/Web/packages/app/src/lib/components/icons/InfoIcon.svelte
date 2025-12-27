<script lang="ts">
  import Info from "lucide-svelte/icons/info";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Info - informational message -->
<Info class={className} {...rest} />
