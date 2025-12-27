<script lang="ts">
  import Syringe from "lucide-svelte/icons/syringe";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Site change icon - infusion site change -->
<Syringe class={className} {...rest} />
