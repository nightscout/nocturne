import { describe, expect, it } from 'vitest';
import { rehypeInstallSteps } from './rehype-install-steps.js';

type Node = { type: string; tagName?: string; properties?: Record<string, unknown>; children?: Node[] };

const heading = (id: string): Node => ({ type: 'element', tagName: 'h2', properties: { id }, children: [] });

function run(tree: Node, installSteps?: Record<string, string>) {
  rehypeInstallSteps()(tree, { path: 'guide.svx', data: { fm: installSteps ? { installSteps } : {} } });
  return tree;
}

describe('rehypeInstallSteps', () => {
  it('marks a listed heading with its step', () => {
    const nginx = heading('nginx');
    run({ type: 'root', children: [nginx, heading('troubleshooting')] }, { nginx: 'nginx' });
    expect(nginx.properties).toEqual({ id: 'nginx', dataInstallStep: 'nginx' });
  });

  it('leaves a heading the frontmatter does not list', () => {
    const other = heading('troubleshooting');
    run({ type: 'root', children: [heading('nginx'), other] }, { nginx: 'nginx' });
    expect(other.properties).toEqual({ id: 'troubleshooting' });
  });

  it('leaves a page without installSteps alone', () => {
    const nginx = heading('nginx');
    run({ type: 'root', children: [nginx] });
    expect(nginx.properties).toEqual({ id: 'nginx' });
  });

  it('fails on a listed id that matches no heading', () => {
    expect(() => run({ type: 'root', children: [heading('nginx')] }, { engine: 'nginx' })).toThrow(
      /engine/,
    );
  });
});
