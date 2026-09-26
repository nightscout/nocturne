import { z } from 'zod';

const componentPropsSchema = z.record(z.string(), z.string());

/**
 * Reads a node's `props` attribute. Malformed JSON throws, as it always has; well-formed JSON
 * that is not a string map reads as no props.
 */
export function parseComponentProps(propsJson: unknown): Record<string, string> {
  if (typeof propsJson !== 'string' || propsJson === '') return {};
  const parsed = componentPropsSchema.safeParse(JSON.parse(propsJson));
  return parsed.success ? parsed.data : {};
}

// A prop name the .svx compiler reads as one attribute; anything else could smuggle in markup.
const SVX_PROP_NAME = /^[A-Za-z_$][\w$-]*$/;

// Characters that end a quoted attribute or start markup or a Svelte expression.
const SVX_ATTRIBUTE_UNSAFE = /["&<>{}]/;

/**
 * A prop as a .svx attribute. A value that could break out of a quoted attribute is emitted as
 * a JavaScript string expression instead, with `<` escaped so no markup survives in it.
 */
function serializeSvxProp(key: string, value: string): string {
  if (value === 'true') return key;
  if (!SVX_ATTRIBUTE_UNSAFE.test(value)) return `${key}="${value}"`;
  return `${key}={${JSON.stringify(value).replace(/</g, '\\u003c')}}`;
}

/**
 * Serialize a SvelteComponent node to .svx component syntax.
 */
export function serializeComponentToSvx(
  componentName: string,
  propsJson: string,
  content?: string,
): string {
  const props = parseComponentProps(propsJson);
  const propsStr = Object.entries(props)
    .filter(([key]) => SVX_PROP_NAME.test(key))
    .map(([key, value]) => serializeSvxProp(key, value))
    .join(' ');

  const tag = propsStr ? `<${componentName} ${propsStr}` : `<${componentName}`;

  if (content) {
    return `${tag}>\n${content}\n</${componentName}>`;
  }
  return `${tag} />`;
}
