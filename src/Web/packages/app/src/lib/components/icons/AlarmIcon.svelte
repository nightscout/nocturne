<script lang="ts">
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Critical alarm - requires immediate attention -->
<AlertTriangle class={className} {...rest} />
