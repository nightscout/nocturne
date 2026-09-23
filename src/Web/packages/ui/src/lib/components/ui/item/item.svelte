<script lang="ts">
  import { cn } from "../../../utils";
  import type { Snippet } from "svelte";
  import type {
    HTMLAnchorAttributes,
    HTMLAttributes,
    HTMLButtonAttributes,
  } from "svelte/elements";
  import {
    itemVariants,
    type ItemVariant,
    type ItemSize,
  } from "./index.js";

  // With href the item is a link, with onclick a button, otherwise static content.
  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    size = "default",
    href,
    onclick,
    type = "button",
    disabled,
    children,
    child,
    ...restProps
  }: HTMLAttributes<HTMLElement> &
    Pick<HTMLAnchorAttributes, "href" | "target" | "rel" | "download"> &
    Pick<HTMLButtonAttributes, "type" | "disabled"> & {
      ref?: HTMLElement | null;
      variant?: ItemVariant;
      size?: ItemSize;
      child?: Snippet<[{ props: Record<string, unknown> }]>;
    } = $props();

  const interactive = $derived(href !== undefined || onclick !== undefined);
  const mergedClasses = $derived(
    cn(itemVariants({ variant, size, interactive }), className)
  );
</script>

{#if child}
  {@render child({ props: { class: mergedClasses, href, onclick, disabled, ...restProps } })}
{:else if href !== undefined}
  <a
    bind:this={ref}
    data-slot="item"
    class={mergedClasses}
    href={disabled ? undefined : href}
    aria-disabled={disabled}
    {onclick}
    {...restProps}
  >
    {@render children?.()}
  </a>
{:else if onclick !== undefined}
  <button
    bind:this={ref}
    data-slot="item"
    class={mergedClasses}
    {type}
    {disabled}
    {onclick}
    {...restProps}
  >
    {@render children?.()}
  </button>
{:else}
  <div bind:this={ref} data-slot="item" class={mergedClasses} {...restProps}>
    {@render children?.()}
  </div>
{/if}
