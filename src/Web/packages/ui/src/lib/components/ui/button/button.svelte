<script lang="ts">
  import { cn } from "../../../utils";
  import { buttonVariants, type ButtonProps } from "./index.js";

  let {
    class: className,
    variant = "default",
    size = "default",
    reveal = false,
    ref = $bindable(null),
    href = undefined,
    type = "button",
    disabled,
    brand,
    children,
    ...restProps
  }: ButtonProps = $props();
</script>

{#if href}
  <a
    bind:this={ref}
    data-slot="button"
    class={cn(buttonVariants({ variant, size, reveal }), className)}
    style:--button-bg={brand?.background}
    style:--button-fg={brand?.foreground}
    href={disabled ? undefined : href}
    aria-disabled={disabled}
    role={disabled ? "link" : undefined}
    tabindex={disabled ? -1 : undefined}
    {...restProps}
  >
    {@render children?.()}
  </a>
{:else}
  <button
    bind:this={ref}
    data-slot="button"
    class={cn(buttonVariants({ variant, size, reveal }), className)}
    style:--button-bg={brand?.background}
    style:--button-fg={brand?.foreground}
    {type}
    {disabled}
    {...restProps}
  >
    {@render children?.()}
  </button>
{/if}
