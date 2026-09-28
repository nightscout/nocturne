import Root from "./input-group.svelte";
import Addon from "./input-group-addon.svelte";
import Input from "./input-group-input.svelte";
import { type VariantProps, tv } from "tailwind-variants";

// An icon or text that sits inside an input's border. Clicking it focuses the input.
export const inputGroupAddonVariants = tv({
  base: "text-muted-foreground flex h-auto cursor-text select-none items-center justify-center gap-2 py-1.5 text-sm font-medium [&>svg:not([class*='size-'])]:size-4",
  variants: {
    align: {
      "inline-start": "order-first pl-3",
      "inline-end": "order-last pr-3",
    },
  },
  defaultVariants: {
    align: "inline-start",
  },
});

export type InputGroupAddonAlign = VariantProps<typeof inputGroupAddonVariants>["align"];

export {
  Root,
  Addon,
  Input,
  //
  Root as InputGroup,
  Addon as InputGroupAddon,
  Input as InputGroupInput,
};
