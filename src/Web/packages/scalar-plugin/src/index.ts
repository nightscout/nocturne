import { mount, unmount } from 'svelte';
import { BLOCK_NAMES, type Block, parseBlocks } from './blocks';
import Callout, { CALLOUT_TYPES, type CalloutType } from './components/Callout.svelte';
import Diagram from './components/Diagram.svelte';
import ResourceList from './components/ResourceList.svelte';
import ScopeTable from './components/ScopeTable.svelte';

type MarkdownHook = (context: { element: HTMLElement; source: string; signal: AbortSignal }) => void | (() => void);

type Mounted = Record<string, unknown>;

const BLOCKS = BLOCK_NAMES.map((name) => `pre > code.language-${name}`).join(', ');
const DIAGRAMS = 'pre > code.language-mermaid';

function calloutType(argument: string): CalloutType {
  return CALLOUT_TYPES.find((type) => type === argument) ?? 'info';
}

function render(block: Block, target: HTMLElement): Mounted {
  switch (block.name) {
    case 'callout':
      return mount(Callout, { target, props: { type: calloutType(block.argument), body: block.body } });
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

/**
 * Replaces each fenced block of a rendered page with its component. Scalar sanitizes markdown
 * before this runs, so a page cannot carry a component tag; a fence survives as a code block.
 * A diagram is read off the code block. The other blocks take an argument on the opening fence,
 * which only the source has, so they are matched to the source in order.
 */
const markdown: MarkdownHook = ({ element, source }) => {
  const mounted: Mounted[] = [];

  for (const code of element.querySelectorAll<HTMLElement>(DIAGRAMS)) {
    const diagram = code.textContent ?? '';
    mounted.push(mount(Diagram, { target: replace(code), props: { source: diagram } }));
  }

  const rendered = [...element.querySelectorAll<HTMLElement>(BLOCKS)];
  const blocks = parseBlocks(source);
  if (rendered.length === blocks.length)
    rendered.forEach((code, index) => mounted.push(render(blocks[index], replace(code))));

  if (mounted.length === 0) return;

  return () => {
    for (const component of mounted) void unmount(component);
  };
};

export default () => ({ name: 'nocturne', extensions: [], markdown });
