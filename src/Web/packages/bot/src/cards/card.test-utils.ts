export interface CardButton {
  id: string;
  value: string | undefined;
}

const str = (value: unknown) => (typeof value === "string" ? value : undefined);

interface CardElement {
  props?: Record<string, unknown>;
  children?: unknown;
}

/** Every element in the tree. Card elements nest through `children`, which holds arrays as well as elements. */
function cardElements(node: unknown, found: CardElement[] = []): CardElement[] {
  if (Array.isArray(node)) {
    for (const child of node) cardElements(child, found);
    return found;
  }
  if (node === null || typeof node !== "object") return found;
  const el = node as CardElement;
  found.push(el);
  return cardElements(el.children, found);
}

const props = (node: unknown) =>
  cardElements(node).map((el) => el.props ?? {});

export function cardButtons(node: unknown): CardButton[] {
  return props(node)
    .filter((p) => typeof p.id === "string")
    .map((p) => ({ id: p.id as string, value: str(p.value) }));
}

export function cardTitle(node: unknown): string | undefined {
  return props(node).map((p) => str(p.title)).find((title) => title !== undefined);
}

/** The text of every element whose sole child is a string, which includes button labels. */
export function cardTexts(node: unknown): string[] {
  return cardElements(node)
    .map((el) =>
      Array.isArray(el.children) && el.children.length === 1
        ? str(el.children[0])
        : undefined,
    )
    .filter((text): text is string => text !== undefined);
}

/** Each `Field` rendered, as `label: value`. */
export function cardFields(node: unknown): string[] {
  return props(node)
    .filter((p) => typeof p.label === "string")
    .map((p) => `${p.label}: ${p.value}`);
}
