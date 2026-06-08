import { type VariantProps, tv } from "tailwind-variants";

export const itemVariants = tv({
  base: "relative flex w-full items-center gap-3 rounded-lg p-3 text-left transition-colors",
  variants: {
    variant: {
      default: "bg-muted/40 border border-border/40",
      outline: "border border-border bg-transparent",
      muted: "bg-muted/20",
      ghost: "",
    },
    size: {
      default: "p-3",
      sm: "p-2 gap-2",
      lg: "p-4 gap-4",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
  },
});

export type ItemVariant = VariantProps<typeof itemVariants>["variant"];
export type ItemSize = VariantProps<typeof itemVariants>["size"];
