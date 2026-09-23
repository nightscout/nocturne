<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { inputGroupAddonVariants, type InputGroupAddonAlign } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    align = "inline-start",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    align?: InputGroupAddonAlign;
  } = $props();

  function focusControl(event: MouseEvent) {
    if (event.target instanceof Element && event.target.closest("button")) return;
    ref?.parentElement
      ?.querySelector<HTMLInputElement>("[data-slot=input-group-control]")
      ?.focus();
  }
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<div
  bind:this={ref}
  data-slot="input-group-addon"
  data-align={align}
  role="group"
  class={cn(inputGroupAddonVariants({ align }), className)}
  onclick={focusControl}
  {...restProps}
>
  {@render children?.()}
</div>
