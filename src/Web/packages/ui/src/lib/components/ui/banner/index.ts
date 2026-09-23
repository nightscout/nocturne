import Root from "./banner.svelte";
import { type VariantProps, tv } from "tailwind-variants";

// A full-width status strip pinned above scrolling content.
export const bannerVariants = tv({
  base: "sticky top-0 z-50 flex items-center justify-between gap-4 border-b px-4 py-2 text-sm [&_svg:not([class*='size-'])]:size-4 [&_svg]:shrink-0",
  variants: {
    variant: {
      warning: "bg-warning-subtle text-warning-subtle-foreground border-warning/30",
      info: "bg-info-subtle text-info-subtle-foreground border-info/30",
    },
  },
  defaultVariants: {
    variant: "info",
  },
});

export type BannerVariant = VariantProps<typeof bannerVariants>["variant"];

export {
  Root,
  //
  Root as Banner,
};
