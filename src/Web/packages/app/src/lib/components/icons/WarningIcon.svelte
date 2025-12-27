<script lang="ts">
  import AlertCircle from "lucide-svelte/icons/alert-circle";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Warning - advisory, may require attention -->
<AlertCircle class={className} {...rest} />
