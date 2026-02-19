import { describe, it, expect } from 'vitest';
import { generateRemoteFunctions } from '../generators/remote-functions.js';
import type { OperationInfo, ParsedSpec } from '../types.js';

function createOperation(overrides: Partial<OperationInfo> = {}): OperationInfo {
  return {
    operationId: 'Foods_GetFavorites',
    tag: 'V4 Foods',
    method: 'get',
    path: '/api/v4/foods/favorites',
    remoteType: 'query',
    invalidates: [],
    parameters: [],
    isVoidResponse: false,
    clientPropertyName: 'foodsV4',
    ...overrides,
  };
}

function getGeneratedFile(parsed: ParsedSpec, fileName: string): string {
  const files = generateRemoteFunctions(parsed);
  const content = files.get(fileName);
  if (!content) {
    const available = Array.from(files.keys()).join(', ');
    throw new Error(`File "${fileName}" not found. Available: ${available}`);
  }
  return content;
}

describe('generateRemoteFunctions', () => {
  describe('imports', () => {
    it('imports redirect from @sveltejs/kit', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation()],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("import { error, redirect } from '@sveltejs/kit';");
    });
  });

  describe('auth error handling in query functions', () => {
    it('includes 401 redirect in no-arg query catch block', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation()],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("const status = (err as any)?.status;");
      expect(content).toContain("if (status === 401) { const { url } = getRequestEvent(); throw redirect(302, `/login?redirectTo=${encodeURIComponent(url.pathname + url.search)}`); }");
      expect(content).toContain("if (status === 403) throw error(403, 'Forbidden');");
    });

    it('includes 401 redirect in parameterized query catch block', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation({
          operationId: 'Foods_GetById',
          parameters: [{ name: 'id', in: 'path', required: true, type: 'string' }],
        })],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("const status = (err as any)?.status;");
      expect(content).toContain("if (status === 401) { const { url } = getRequestEvent(); throw redirect(302, `/login?redirectTo=${encodeURIComponent(url.pathname + url.search)}`); }");
      expect(content).toContain("if (status === 403) throw error(403, 'Forbidden');");
    });

    it('preserves existing error(500) fallback in query', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation()],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("throw error(500, 'Failed to get favorites');");
    });
  });

  describe('auth error handling in command functions', () => {
    it('includes 401 redirect in no-arg command catch block', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation({
          operationId: 'Foods_SyncAll',
          remoteType: 'command',
          isVoidResponse: true,
        })],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("const status = (err as any)?.status;");
      expect(content).toContain("if (status === 401) { const { url } = getRequestEvent(); throw redirect(302, `/login?redirectTo=${encodeURIComponent(url.pathname + url.search)}`); }");
      expect(content).toContain("if (status === 403) throw error(403, 'Forbidden');");
    });

    it('includes 401 redirect in parameterized command catch block', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation({
          operationId: 'Foods_DeleteFood',
          remoteType: 'command',
          isVoidResponse: true,
          parameters: [{ name: 'id', in: 'path', required: true, type: 'string' }],
        })],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("const status = (err as any)?.status;");
      expect(content).toContain("if (status === 401) { const { url } = getRequestEvent(); throw redirect(302, `/login?redirectTo=${encodeURIComponent(url.pathname + url.search)}`); }");
      expect(content).toContain("if (status === 403) throw error(403, 'Forbidden');");
    });

    it('preserves existing error(500) fallback in command', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation({
          operationId: 'Foods_DeleteFood',
          remoteType: 'command',
          isVoidResponse: true,
          parameters: [{ name: 'id', in: 'path', required: true, type: 'string' }],
        })],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      expect(content).toContain("throw error(500, 'Failed to delete food');");
    });
  });

  describe('catch block ordering', () => {
    it('places auth checks before console.error in query', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation()],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      const statusIndex = content.indexOf("const status = (err as any)?.status;");
      const redirectIndex = content.indexOf("if (status === 401) {");
      const forbiddenIndex = content.indexOf("if (status === 403) throw error(403, 'Forbidden');");
      const consoleIndex = content.indexOf("console.error('Error in foodsV4.getFavorites:', err);");
      const error500Index = content.indexOf("throw error(500, 'Failed to get favorites');");

      expect(statusIndex).toBeLessThan(redirectIndex);
      expect(redirectIndex).toBeLessThan(forbiddenIndex);
      expect(forbiddenIndex).toBeLessThan(consoleIndex);
      expect(consoleIndex).toBeLessThan(error500Index);
    });

    it('places auth checks before console.error in command', () => {
      const parsed: ParsedSpec = {
        operations: [createOperation({
          operationId: 'Foods_SyncAll',
          remoteType: 'command',
          isVoidResponse: true,
        })],
        tags: ['V4 Foods'],
      };

      const content = getGeneratedFile(parsed, 'foods.generated.remote.ts');
      const statusIndex = content.indexOf("const status = (err as any)?.status;");
      const redirectIndex = content.indexOf("if (status === 401) {");
      const forbiddenIndex = content.indexOf("if (status === 403) throw error(403, 'Forbidden');");
      const consoleIndex = content.indexOf("console.error('Error in foodsV4.syncAll:', err);");
      const error500Index = content.indexOf("throw error(500, 'Failed to sync all');");

      expect(statusIndex).toBeLessThan(redirectIndex);
      expect(redirectIndex).toBeLessThan(forbiddenIndex);
      expect(forbiddenIndex).toBeLessThan(consoleIndex);
      expect(consoleIndex).toBeLessThan(error500Index);
    });
  });
});
