import Root from "./item.svelte";
import Content from "./item-content.svelte";
import Title from "./item-title.svelte";
import Description from "./item-description.svelte";
import Media from "./item-media.svelte";
import Actions from "./item-actions.svelte";
import Group from "./item-group.svelte";

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

export { itemVariants, type ItemVariant, type ItemSize } from "./item-variants";
export {
	itemMediaVariants,
	type ItemMediaVariant,
} from "./item-media-variants";
