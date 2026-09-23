import Root from "./item.svelte";
import Content from "./item-content.svelte";
import Title from "./item-title.svelte";
import Description from "./item-description.svelte";
import Media from "./item-media.svelte";
import Actions from "./item-actions.svelte";
import Group from "./item-group.svelte";
import { type VariantProps, tv } from "tailwind-variants";

export const itemVariants = tv({
  base: "relative flex w-full items-center gap-3 rounded-lg text-left outline-none transition-colors focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 aria-busy:ring-2 aria-busy:ring-primary aria-[current=true]:border-primary aria-[current=true]:bg-primary/5",
  variants: {
    variant: {
      default: "bg-muted/40 border border-border/40",
      outline: "border border-border bg-transparent",
      muted: "bg-muted",
      ghost: "",
      // A connected or configured source.
      success: "border border-success/30 bg-success/5",
      // An option that adds something, like the add-item placeholder at the end of a list.
      dashed: "border border-dashed",
    },
    size: {
      default: "p-3",
      sm: "p-2 gap-2",
      lg: "p-4 gap-4",
    },
    // Set by Item from href or onclick; callers do not pass it.
    interactive: {
      true: "group cursor-pointer disabled:cursor-not-allowed disabled:opacity-60",
      false: "",
    },
  },
  compoundVariants: [
    { interactive: true, variant: ["default", "outline", "ghost"], class: "hover:not-disabled:bg-accent/50" },
    { interactive: true, variant: "outline", class: "hover:not-disabled:border-primary/50" },
    { interactive: true, variant: "muted", class: "hover:not-disabled:bg-muted/80" },
    { interactive: true, variant: "success", class: "hover:not-disabled:bg-success/10" },
    { interactive: true, variant: "dashed", class: "hover:not-disabled:border-primary hover:not-disabled:bg-accent" },
  ],
  defaultVariants: {
    variant: "default",
    size: "default",
    interactive: false,
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

