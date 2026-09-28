<script lang="ts">
  import Waves from "@lucide/svelte/icons/waves-horizontal";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Liberty mode - relaxed closed-loop -->
<Waves class={className} {...rest} />
