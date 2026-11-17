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
