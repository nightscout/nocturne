import { type VariantProps, tv } from "tailwind-variants";

export const labelVariants = tv({
  base: "flex select-none items-center gap-2 font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-50 group-data-[disabled=true]:pointer-events-none group-data-[disabled=true]:opacity-50",
  variants: {
    size: {
      default: "text-sm",
      // Dense editors, and the caption over a compact (xs) control.
      sm: "text-xs",
      // A setting's title above its description, e.g. beside a Switch.
      lg: "text-base",
    },
    variant: {
      default: "",
      // Secondary captions: units, read-only facts, sub-fields.
      muted: "text-muted-foreground",
      // The text of a checkbox, radio or switch choice, set lighter than field labels.
      option: "font-normal",
    },
  },
  defaultVariants: {
    size: "default",
    variant: "default",
  },
});

export type LabelSize = VariantProps<typeof labelVariants>["size"];
export type LabelVariant = VariantProps<typeof labelVariants>["variant"];
