<script lang="ts">
  import AlertTriangle from "lucide-svelte/icons/alert-triangle";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Critical alarm - requires immediate attention -->
<AlertTriangle class={className} {...rest} />
