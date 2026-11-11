/** The fenced blocks a page of the reference can place, by the language of the fence. */
export const BLOCK_NAMES = ['callout', 'scopes', 'resources'] as const;

export type BlockName = (typeof BLOCK_NAMES)[number];

export type Block = {
  name: BlockName;
  /** What follows the name on the opening fence: `tip`, or the name of a sidebar group. */
  argument: string;
  body: string;
};

// A fence of three or more backticks, indented at most three spaces, closed by a fence at least
// as long. A page that fences a block another way is rendered by Scalar but not found here, and
// the plugin then leaves the blocks of that name alone.
const FENCE = new RegExp(
  String.raw`^ {0,3}(\`{3,})(${BLOCK_NAMES.join('|')})(?:[ \t]+([^\n]*))?\n([\s\S]*?)^ {0,3}\1\`*[ \t]*$`,
  'gm',
);

/** The blocks of a markdown source, in order. Scalar keeps that order in the rendered page. */
export function parseBlocks(source: string): Block[] {
  return [...source.replace(/\r\n/g, '\n').matchAll(FENCE)].map(([, , name, argument, body]) => ({
    name: name as BlockName,
    argument: (argument ?? '').trim(),
    body: body.trim(),
  }));
}

/**
 * Pairs the rendered code blocks of one name with the source blocks of that name, in order.
 * Null when the two counts differ: a pairing by position would then put an argument on the
 * wrong block.
 */
export function pairBlocks<Rendered>(name: BlockName, rendered: Rendered[], blocks: Block[]): [Rendered, Block][] | null {
  const parsed = blocks.filter((block) => block.name === name);
  return parsed.length === rendered.length ? rendered.map((element, index) => [element, parsed[index]]) : null;
}

export type Inline = { kind: 'text' | 'code' | 'strong'; text: string } | { kind: 'link'; text: string; href: string };

const INLINE = /`([^`]+)`|\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)\s]+)\)/g;

/**
 * The inline markdown a block body may use. The body never passes through Scalar's sanitizer,
 * so it is turned into tokens that a component renders as elements, never into HTML.
 */
export function parseInline(text: string): Inline[] {
  const tokens: Inline[] = [];
  let last = 0;

  for (const match of text.matchAll(INLINE)) {
    if (match.index > last) tokens.push({ kind: 'text', text: text.slice(last, match.index) });

    if (match[1] !== undefined) tokens.push({ kind: 'code', text: match[1] });
    else if (match[2] !== undefined) tokens.push({ kind: 'strong', text: match[2] });
    else if (isSafeHref(match[4])) tokens.push({ kind: 'link', text: match[3], href: match[4] });
    else tokens.push({ kind: 'text', text: match[3] });

    last = match.index + match[0].length;
  }

  if (last < text.length) tokens.push({ kind: 'text', text: text.slice(last) });
  return tokens;
}

/** An `https` URL, an anchor, or a path on this site. `//host` and `/\host` name another site. */
export function isSafeHref(href: string): boolean {
  return /^(https:\/\/|#|\/(?![/\\]))/.test(href);
}
