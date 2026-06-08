import { type VariantProps, tv } from "tailwind-variants";

export const itemMediaVariants = tv({
  base: "flex shrink-0 items-center justify-center",
  variants: {
    variant: {
      default: "",
      icon: "size-9 rounded-lg bg-muted text-muted-foreground",
      avatar: "size-10 overflow-hidden rounded-full",
      image: "size-12 overflow-hidden rounded-md",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type ItemMediaVariant = VariantProps<
  typeof itemMediaVariants
>["variant"];
