import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HostingChoice } from './hosting.svelte.js';

const KEY = 'nocturne-docs:hosting';

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: (key) => void values.delete(key),
    clear: () => values.clear(),
    key: () => null,
    get length() {
      return values.size;
    },
  };
}

function blockedStorage(): Storage {
  const blocked = () => {
    throw new DOMException('blocked', 'SecurityError');
  };
  return { ...memoryStorage(), getItem: blocked, setItem: blocked };
}

beforeEach(() => {
  vi.stubGlobal('localStorage', memoryStorage());
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('HostingChoice', () => {
  it('shows both paths until the reader chooses', () => {
    const choice = new HostingChoice();
    expect(choice.current).toBeNull();
    expect(choice.shows('managed')).toBe(true);
    expect(choice.shows('self-host')).toBe(true);
  });

  it('shows only the chosen path once the reader chooses', () => {
    const choice = new HostingChoice();
    choice.choose('managed');
    expect(choice.shows('managed')).toBe(true);
    expect(choice.shows('self-host')).toBe(false);
  });

  it('always shows content that is for neither path', () => {
    const choice = new HostingChoice();
    choice.choose('self-host');
    expect(choice.shows(undefined)).toBe(true);
  });

  it('remembers the choice for the next page', () => {
    new HostingChoice().choose('self-host');
    expect(localStorage.getItem(KEY)).toBe('self-host');

    const next = new HostingChoice();
    next.start();
    expect(next.started).toBe(true);
    expect(next.current).toBe('self-host');
  });

  it('ignores a stored value that is not a path', () => {
    localStorage.setItem(KEY, 'cloud');
    const choice = new HostingChoice();
    choice.start();
    expect(choice.started).toBe(true);
    expect(choice.current).toBeNull();
  });

  it('reads storage only on the first start', () => {
    const choice = new HostingChoice();
    choice.start();
    localStorage.setItem(KEY, 'managed');
    choice.start();
    expect(choice.current).toBeNull();
  });

  it('still works for the page load when storage is blocked', () => {
    vi.stubGlobal('localStorage', blockedStorage());
    const choice = new HostingChoice();
    choice.start();
    expect(choice.started).toBe(true);
    choice.choose('managed');
    expect(choice.current).toBe('managed');
    expect(choice.shows('self-host')).toBe(false);
  });
});
