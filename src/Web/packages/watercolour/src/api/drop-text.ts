import { layoutWithLines, prepareWithSegments } from '@chenglou/pretext';
import type { Box } from './drop-stroke';

/**
 * A font a surface's text is set in, as Pretext needs it.
 *
 * `font` is a CSS `font` shorthand and **has to match the host's own CSS
 * exactly**, down to the weight and the stack. Pretext measures off that
 * string, so a mismatch returns line boxes for text that was never drawn, and
 * marks then land on the copy. It is a required prop rather than something
 * read back off the element: reading it back is the very layout cost the
 * off-DOM measurement exists to avoid.
 */
export interface DropFont {
  font: string;
  /** The rendered line height in px, as the host's CSS sets it. */
  lineHeight: number;
  /** How the lines sit in their box; only `start` and `center` occur in practice. */
  align?: 'start' | 'center' | 'end';
}

/**
 * The fonts a surface uses, keyed by the `data-drop-text` value on the
 * elements set in them.
 */
export type DropFonts = Readonly<Record<string, DropFont>>;

/** Space held around a measured box, so a mark never crowds a line. */
export const DROP_TEXT_PAD = 8;

function offsetWithin(el: HTMLElement, root: HTMLElement): { x: number; y: number } {
  let x = 0;
  let y = 0;
  for (let node: HTMLElement | null = el; node && node !== root; node = node.offsetParent as HTMLElement | null) {
    x += node.offsetLeft;
    y += node.offsetTop;
  }
  return { x, y };
}

/**
 * Line boxes for one element, laid out off the DOM.
 *
 * Pretext returns the width of every line, which is the whole point. A title
 * that stops at 154 px in a 266 px column blocks 154 px, so the space beside
 * it stays usable. An element box would have blocked the full column.
 */
function pretextBoxes(el: HTMLElement, root: HTMLElement, font: DropFont, pad: number): Box[] {
  const text = el.textContent?.trim();
  const width = el.offsetWidth;
  if (!text || width < 1) return [];
  const { x, y } = offsetWithin(el, root);
  const prepared = prepareWithSegments(text, font.font);
  const align = font.align ?? 'start';
  return layoutWithLines(prepared, width, font.lineHeight).lines.map((line, i) => {
    const w = Math.min(Math.ceil(line.width), width);
    const inset = align === 'center' ? (width - w) / 2 : align === 'end' ? width - w : 0;
    return {
      x: x + inset - pad,
      y: y + i * font.lineHeight - pad,
      w: w + pad * 2,
      h: font.lineHeight + pad * 2,
    };
  });
}

/**
 * The line boxes of every text node under `root`, from `Range.getClientRects`.
 *
 * Correct, and one layout read per text node. It is the fallback for content
 * Pretext was given no font for, and for anything that is not text.
 */
export function textBoxes(root: HTMLElement, pad = DROP_TEXT_PAD, skip?: readonly HTMLElement[]): Box[] {
  const origin = root.getBoundingClientRect();
  const boxes: Box[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const range = document.createRange();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (!node.textContent?.trim()) continue;
    if (skip?.some((el) => el.contains(node))) continue;
    range.selectNodeContents(node);
    for (const r of range.getClientRects()) {
      if (r.width < 1 || r.height < 1) continue;
      boxes.push({
        x: r.left - origin.left - pad,
        y: r.top - origin.top - pad,
        w: r.width + pad * 2,
        h: r.height + pad * 2,
      });
    }
  }
  return boxes;
}

/**
 * Everything on a surface a mark has to stay clear of.
 *
 * Text goes through Pretext when the host declared the font it is set in, and
 * through `Range.getClientRects` when it did not.
 *
 * Anything that is not text — an icon, an avatar, an image — carries
 * `data-drop-obstacle`. It is measured as one box, having no lines to find.
 */
export function measureObstacles(root: HTMLElement, fonts?: DropFonts, pad = DROP_TEXT_PAD): Box[] {
  const boxes: Box[] = [];
  const measured: HTMLElement[] = [];

  if (fonts) {
    for (const el of root.querySelectorAll<HTMLElement>('[data-drop-text]')) {
      const font = fonts[el.dataset.dropText ?? ''];
      if (!font) continue;
      measured.push(el);
      boxes.push(...pretextBoxes(el, root, font, pad));
    }
  }

  boxes.push(...textBoxes(root, pad, measured));

  const origin = root.getBoundingClientRect();
  for (const el of root.querySelectorAll<HTMLElement>('[data-drop-obstacle]')) {
    const r = el.getBoundingClientRect();
    boxes.push({
      x: r.left - origin.left - pad,
      y: r.top - origin.top - pad,
      w: r.width + pad * 2,
      h: r.height + pad * 2,
    });
  }
  return boxes;
}
