import Root from "./card.svelte";
import Content from "./card-content.svelte";
import Description from "./card-description.svelte";
import Footer from "./card-footer.svelte";
import Header from "./card-header.svelte";
import Title from "./card-title.svelte";
import Action from "./card-action.svelte";
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
      // A highlighted summary, e.g. a report's headline figures.
      primary: "border-primary/20 bg-primary/5",
      // Supporting notes or a filter bar beside the main content.
      muted: "bg-muted/30",
      // An empty state standing in for content that has yet to be added.
      dashed: "border-dashed",
    },
    size: {
      default: "",
      // A stat tile whose figure and label sit directly in the card, without Card.Content.
      sm: "p-4",
      // A panel split into ruled cells that carry their own padding, e.g. a row of figures.
      flush: "gap-0 overflow-hidden py-0",
    },
    // A card that is itself a link, wrapped in an <a>.
    interactive: {
      true: "transition-colors hover:bg-accent/50",
      false: "",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
    interactive: false,
  },
});

export type CardVariant = VariantProps<typeof cardVariants>["variant"];
export type CardSize = VariantProps<typeof cardVariants>["size"];

export const cardHeaderVariants = tv({
  base: "@container/card-header has-data-[slot=card-action]:grid-cols-[1fr_auto] [.border-b]:pb-6 grid auto-rows-min grid-rows-[auto_auto] items-start gap-1.5 px-6",
  variants: {
    // A header that is the trigger of a collapsible card.
    interactive: {
      true: "cursor-pointer transition-colors hover:bg-accent/50",
      false: "",
    },
  },
  defaultVariants: {
    interactive: false,
  },
});

export const cardTitleVariants = tv({
  base: "font-semibold leading-none",
  variants: {
    variant: {
      default: "",
      // The label of a stat tile, above its figure.
      muted: "text-muted-foreground",
      destructive: "text-destructive",
      success: "text-success",
      warning: "text-warning",
      info: "text-info",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type CardTitleVariant = VariantProps<typeof cardTitleVariants>["variant"];

export const cardDescriptionVariants = tv({
  base: "text-muted-foreground text-sm",
  variants: {
    size: {
      default: "",
      sm: "text-xs",
    },
  },
  defaultVariants: {
    size: "default",
  },
});

export type CardDescriptionSize = VariantProps<typeof cardDescriptionVariants>["size"];

export const cardContentVariants = tv({
  base: "px-6",
  variants: {
    variant: {
      default: "",
      // A secondary message, e.g. an empty or error state, in the card's description type.
      muted: "text-muted-foreground text-sm",
    },
    size: {
      default: "",
      // Explanatory prose, e.g. a report's "about this report" card.
      sm: "text-sm",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
  },
});

export const cardFooterVariants = tv({
  base: "[.border-t]:pt-6 flex items-center px-6",
  variants: {
    variant: {
      default: "",
      // A footnote on the card's content, e.g. that a list is truncated.
      muted: "text-muted-foreground text-sm",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type CardFooterVariant = VariantProps<typeof cardFooterVariants>["variant"];

export type CardContentVariant = VariantProps<typeof cardContentVariants>["variant"];
export type CardContentSize = VariantProps<typeof cardContentVariants>["size"];

export {
	Root,
	Content,
	Description,
	Footer,
	Header,
	Title,
	Action,
	//
	Root as Card,
	Content as CardContent,
	Description as CardDescription,
	Footer as CardFooter,
	Header as CardHeader,
	Title as CardTitle,
	Action as CardAction,
};
