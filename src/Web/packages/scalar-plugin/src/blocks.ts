/** The fenced blocks a page of the reference can place, by the language of the fence. */
export const BLOCK_NAMES = ['callout', 'scopes', 'resources'] as const;

export type BlockName = (typeof BLOCK_NAMES)[number];

export type Block = {
  name: BlockName;
  /** What follows the name on the opening fence: `tip`, or the name of a sidebar group. */
  argument: string;
  body: string;
};

const FENCE = new RegExp(
  String.raw`^\`\`\`(${BLOCK_NAMES.join('|')})[ \t]*([^\n]*)\n([\s\S]*?)^\`\`\`[ \t]*$`,
  'gm',
);

/** The blocks of a markdown source, in order. Scalar keeps that order in the rendered page. */
export function parseBlocks(source: string): Block[] {
  return [...source.replace(/\r\n/g, '\n').matchAll(FENCE)].map(([, name, argument, body]) => ({
    name: name as BlockName,
    argument: argument.trim(),
    body: body.trim(),
  }));
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

function isSafeHref(href: string): boolean {
  return /^(https?:\/\/|#|\/)/.test(href);
}
