import Root from "./item.svelte";
import Content from "./item-content.svelte";
import Title from "./item-title.svelte";
import Description from "./item-description.svelte";
import Media from "./item-media.svelte";
import Actions from "./item-actions.svelte";
import Group from "./item-group.svelte";
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

export {
	Root,
	Content,
	Title,
	Description,
	Media,
	Actions,
	Group,
	//
	Root as Item,
	Content as ItemContent,
	Title as ItemTitle,
	Description as ItemDescription,
	Media as ItemMedia,
	Actions as ItemActions,
	Group as ItemGroup,
};

