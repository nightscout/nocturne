import { Marked } from "marked";

const SAFE_PROTOCOLS = new Set(["http:", "https:", "mailto:"]);

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function isSafeUrl(href: string): boolean {
  try {
    return SAFE_PROTOCOLS.has(new URL(href, "https://github.com/").protocol);
  } catch {
    return false;
  }
}

/**
 * Release bodies come live from the GitHub API and quote contributors' PR titles. marked passes
 * HTML through untouched, so here raw HTML renders as text. A link or image to anything but
 * http(s) or mailto keeps only its text.
 */
const releaseMarked = new Marked({
  breaks: true,
  renderer: {
    html({ text }) {
      return escapeHtml(text);
    },
    link(token) {
      return isSafeUrl(token.href) ? false : this.parser.parseInline(token.tokens);
    },
    image(token) {
      return isSafeUrl(token.href) ? false : escapeHtml(token.text);
    },
  },
});

export function renderReleaseMarkdown(body: string | null): string {
  if (!body) return "";
  return releaseMarked.parse(body, { async: false });
}
