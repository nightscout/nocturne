import { visit } from 'unist-util-visit';
import type { Plugin } from 'unified';
import type { Root, Text } from 'mdast';

const VAR_PATTERN = /\{var:([a-zA-Z_][a-zA-Z0-9_]*)\}/g;

export const remarkVars: Plugin<[], Root> = () => {
  return (tree) => {
    visit(tree, 'text', (node: Text, index, parent) => {
      if (!VAR_PATTERN.test(node.value)) return;

      const parts: Array<{ type: 'text' | 'html'; value: string }> = [];
      let lastIndex = 0;

      VAR_PATTERN.lastIndex = 0;
      let match: RegExpExecArray | null;

      while ((match = VAR_PATTERN.exec(node.value)) !== null) {
        if (match.index > lastIndex) {
          parts.push({ type: 'text', value: node.value.slice(lastIndex, match.index) });
        }
        parts.push({ type: 'html', value: `{{${match[1]}}}` });
        lastIndex = match.index + match[0].length;
      }

      if (lastIndex < node.value.length) {
        parts.push({ type: 'text', value: node.value.slice(lastIndex) });
      }

      if (parts.length > 0 && parent && typeof index === 'number') {
        parent.children.splice(index, 1, ...parts);
      }
    });
  };
};
