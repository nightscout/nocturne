export interface CardButton {
  id: string;
  value: string | undefined;
}

/** Card elements nest through `children`, which holds arrays as well as elements. */
export function cardButtons(node: unknown, found: CardButton[] = []): CardButton[] {
  if (Array.isArray(node)) {
    for (const child of node) cardButtons(child, found);
    return found;
  }
  if (node === null || typeof node !== "object") return found;
  const el = node as { props?: Record<string, unknown>; children?: unknown };
  if (typeof el.props?.id === "string") {
    found.push({ id: el.props.id, value: el.props.value as string | undefined });
  }
  return cardButtons(el.children, found);
}
