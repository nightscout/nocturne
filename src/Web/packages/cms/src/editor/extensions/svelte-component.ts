import { Node, mergeAttributes, type Editor } from '@tiptap/core';
import { parseComponentProps } from './svelte-component-svx.ts';
import { registerComponentActions, ComponentIcon } from '../../lib/components/edra/extensions/slash-command/groups.ts';

export { parseComponentProps, serializeComponentToSvx } from './svelte-component-svx.ts';

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    svelteComponent: {
      insertSvelteComponent: (name: string, props?: Record<string, string>) => ReturnType;
      updateSvelteComponentProps: (props: Record<string, string>) => ReturnType;
    };
  }
}

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
        editor.commands.insertSvelteComponent(comp.name, comp.defaultProps);
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
      const props = parseComponentProps(propsJson);
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
      return {
        insertSvelteComponent:
          (name, props) =>
          ({ commands }) => {
            return commands.insertContent({
              type: this.name,
              attrs: {
                componentName: name,
                props: JSON.stringify(props ?? {}),
              },
            });
          },
        updateSvelteComponentProps:
          (props) =>
          ({ tr, state }) => {
            const { selection } = state;
            const node = state.doc.nodeAt(selection.from);
            if (node?.type.name !== 'svelteComponent') return false;
            const existing = parseComponentProps(node.attrs.props);
            tr.setNodeMarkup(selection.from, undefined, {
              ...node.attrs,
              props: JSON.stringify({ ...existing, ...props }),
            });
            return true;
          },
      };
    },
  });
};

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
