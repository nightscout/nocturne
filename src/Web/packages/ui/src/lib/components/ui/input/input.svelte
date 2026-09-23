<script lang="ts">
  import type {
    HTMLInputAttributes,
    HTMLInputTypeAttribute,
  } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import {
    inputVariants,
    type InputSize,
    type InputVariant,
  } from "./index.js";

  type InputType = Exclude<HTMLInputTypeAttribute, "file">;

  // The native `size` attribute (width in characters) gives way to the design-system size.
  type Props = WithElementRef<
    Omit<HTMLInputAttributes, "type" | "size"> &
      (
        | { type: "file"; files?: FileList }
        | { type?: InputType; files?: undefined }
      ) & {
        size?: InputSize;
        variant?: InputVariant;
        /** Marks a value confirmed as acceptable; aria-invalid still wins. */
        valid?: boolean;
      }
  >;

  let {
    ref = $bindable(null),
    value = $bindable(),
    type,
    files = $bindable(),
    size = "default",
    variant = "default",
    valid = false,
    class: className,
    ...restProps
  }: Props = $props();
</script>

{#if type === "file"}
  <input
    bind:this={ref}
    data-slot="input"
    data-size={size}
    data-valid={valid || undefined}
    class={cn(
      inputVariants({ size, variant }),
      "bg-transparent py-2 font-medium",
      size === "default" && "text-sm",
      className
    )}
    type="file"
    bind:files
    bind:value
    {...restProps}
  />
{:else}
  <input
    bind:this={ref}
    data-slot="input"
    data-size={size}
    data-valid={valid || undefined}
    class={cn(inputVariants({ size, variant }), "bg-background py-1", className)}
    {type}
    bind:value
    {...restProps}
  />
{/if}
