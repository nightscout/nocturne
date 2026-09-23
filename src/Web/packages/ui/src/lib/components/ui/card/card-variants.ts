import { type VariantProps, tv } from "tailwind-variants";

export const cardVariants = tv({
  base: "bg-card text-card-foreground flex flex-col gap-6 rounded-xl border py-6 shadow-sm",
  variants: {
    variant: {
      default: "",
      destructive: "border-destructive/50 bg-destructive/5",
      success: "border-success/30 bg-success/5",
      warning: "border-warning/30 bg-warning/5",
      info: "border-info/30 bg-info/5",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type CardVariant = VariantProps<typeof cardVariants>["variant"];
