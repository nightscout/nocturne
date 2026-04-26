import { Node, mergeAttributes, type Editor, type RawCommands } from '@tiptap/core';
import { registerComponentActions, ComponentIcon } from '../../lib/components/edra/extensions/slash-command/groups.ts';

export interface ComponentDefinition {
  name: string;
  label: string;
  /** Import path for the .svx script block */
  importPath: string;
  /** Default props when inserting */
  defaultProps?: Record<string, string>;
  /** Whether the component has children content */
  hasContent?: boolean;
}

export const SvelteComponentExtension = (components: ComponentDefinition[]) => {
  // Register slash command entries for each component
  registerComponentActions(
    components.map((comp) => ({
      name: `component-${comp.name}`,
      icon: ComponentIcon,
      tooltip: comp.label,
      onClick: (editor: Editor) => {
        (editor.commands as any).insertSvelteComponent(comp.name, comp.defaultProps);
      },
    })),
  );

  return Node.create({
    name: 'svelteComponent',
    group: 'block',
    atom: true,

    addAttributes() {
      return {
        componentName: { default: '' },
        props: { default: '{}' },
      };
    },

    parseHTML() {
      return components.map((comp) => ({
        tag: comp.name,
        getAttrs: (dom: HTMLElement) => {
          const props: Record<string, string> = {};
          for (const attr of dom.attributes) {
            if (attr.name !== 'data-component') {
              props[attr.name] = attr.value;
            }
          }
          return { componentName: comp.name, props: JSON.stringify(props) };
        },
      }));
    },

    renderHTML({ HTMLAttributes }) {
      const { componentName, props: propsJson, ...rest } = HTMLAttributes;
      const props = JSON.parse(propsJson || '{}') as Record<string, string>;
      const propsDisplay = Object.entries(props)
        .map(([k, v]) => `${k}="${v}"`)
        .join(' ');
      const label = propsDisplay ? `<${componentName} ${propsDisplay} />` : `<${componentName} />`;
      return [
        'div',
        mergeAttributes(rest, {
          'data-svelte-component': componentName,
          'data-component-props': propsJson,
          class: 'svelte-component-block',
        }),
        label,
      ];
    },

    addCommands() {
      return ({
        insertSvelteComponent:
          (name: string, props?: Record<string, string>) =>
          ({ commands }: { commands: Record<string, (...args: any[]) => any> }) => {
            return commands.insertContent({
              type: this.name,
              attrs: {
                componentName: name,
                props: JSON.stringify(props ?? {}),
              },
            });
          },
        updateSvelteComponentProps:
          (props: Record<string, string>) =>
          ({ tr, state }: { tr: any; state: any }) => {
            const { selection } = state;
            const node = state.doc.nodeAt(selection.from);
            if (node?.type.name !== 'svelteComponent') return false;
            const existing = JSON.parse(node.attrs.props || '{}');
            tr.setNodeMarkup(selection.from, undefined, {
              ...node.attrs,
              props: JSON.stringify({ ...existing, ...props }),
            });
            return true;
          },
      }) as Partial<RawCommands>;
    },
  });
};

/**
 * Serialize a SvelteComponent node to .svx component syntax.
 */
export function serializeComponentToSvx(
  componentName: string,
  propsJson: string,
  content?: string,
): string {
  const props = JSON.parse(propsJson || '{}') as Record<string, string>;
  const propsStr = Object.entries(props)
    .map(([key, value]) => {
      if (value === 'true') return key;
      return `${key}="${value}"`;
    })
    .join(' ');

  const tag = propsStr ? `<${componentName} ${propsStr}` : `<${componentName}`;

  if (content) {
    return `${tag}>\n${content}\n</${componentName}>`;
  }
  return `${tag} />`;
}

/**
 * Collect unique imports needed for the .svx file based on which components are used.
 */
export function collectImports(
  usedComponents: string[],
  registry: ComponentDefinition[],
): string[] {
  return usedComponents
    .map((name) => {
      const def = registry.find((c) => c.name === name);
      if (!def) return null;
      return `${def.name} from '${def.importPath}'`;
    })
    .filter((x): x is string => x !== null);
}
