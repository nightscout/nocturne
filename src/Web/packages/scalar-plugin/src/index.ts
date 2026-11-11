import { mount, unmount } from 'svelte';
import { BLOCK_NAMES, type Block, pairBlocks, parseBlocks } from './blocks';
import Callout, { CALLOUT_TYPES, type CalloutType } from './components/Callout.svelte';
import Diagram from './components/Diagram.svelte';
import ResourceList from './components/ResourceList.svelte';
import ScopeTable from './components/ScopeTable.svelte';

type MarkdownHook = (context: { element: HTMLElement; source: string; signal: AbortSignal }) => void | (() => void);

type Mounted = Record<string, unknown>;

function calloutType(argument: string): CalloutType {
  return CALLOUT_TYPES.find((type) => type === argument) ?? 'info';
}

function render(block: Block, body: string, target: HTMLElement): Mounted {
  switch (block.name) {
    case 'callout':
      return mount(Callout, { target, props: { type: calloutType(block.argument), body } });
    case 'scopes':
      return mount(ScopeTable, { target });
    case 'resources':
      return mount(ResourceList, { target, props: { group: block.argument } });
  }
}

/** Puts a host in place of a rendered code block, and returns it. */
function replace(code: HTMLElement): HTMLElement {
  const host = document.createElement('div');
  // Scalar hides a block marked this way with the rest of a collapsed description.
  host.dataset.markdownBlock = '';
  code.parentElement!.replaceWith(host);
  return host;
}

function rendered(element: HTMLElement, language: string): HTMLElement[] {
  return [...element.querySelectorAll<HTMLElement>(`pre > code.language-${language}`)];
}

/**
 * Replaces each fenced block of a rendered page with its component. Scalar sanitizes markdown
 * before this runs, so a page cannot carry a component tag; a fence survives as a code block.
 * The body of a block is read off the code block. Its argument is on the opening fence, which
 * only the source has, so the blocks of each name are paired with the source in order.
 */
const markdown: MarkdownHook = ({ element, source }) => {
  const mounted: Mounted[] = [];

  for (const code of rendered(element, 'mermaid'))
    mounted.push(mount(Diagram, { target: replace(code), props: { source: code.textContent ?? '' } }));

  const blocks = parseBlocks(source);
  for (const name of BLOCK_NAMES) {
    const codes = rendered(element, name);
    if (codes.length === 0) continue;

    const pairs = pairBlocks(name, codes, blocks);
    if (!pairs) {
      console.warn(`[nocturne] The "${name}" blocks of a page are left as code: the page fences one in a form the plugin does not read.`);
      continue;
    }

    for (const [code, block] of pairs)
      mounted.push(render(block, (code.textContent ?? '').trim(), replace(code)));
  }

  if (mounted.length === 0) return;

  return () => {
    for (const component of mounted) void unmount(component);
  };
};

export default () => ({ name: 'nocturne', extensions: [], markdown });
