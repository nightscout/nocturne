import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fillEnvTemplate, InstallGuide, isDomain } from './guide.svelte.js';

const TEMPLATE = [
  '# Required',
  'BASE_DOMAIN=',
  'INSTANCE_KEY=',
  'POSTGRES_PASSWORD=',
  'POSTGRES_USERNAME=nocturne',
  '# WATCHTOWER_API_TOKEN=',
  '# DISCORD_BOT_TOKEN=',
].join('\n');

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

beforeEach(() => {
  vi.stubGlobal('localStorage', memoryStorage());
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('isDomain', () => {
  it.each(['example.com', 'nocturne.example.com', 'smith.duckdns.org', 'a-b.example.co'])('accepts %s', (value) => {
    expect(isDomain(value)).toBe(true);
  });

  it.each([
    '',
    'localhost',
    'https://example.com',
    'example.com/path',
    'example.com:8080',
    '203.0.113.7',
    '-bad.example.com',
    'bad-.example.com',
    'exa mple.com',
    'example..com',
    'javascript:alert(1)',
  ])('refuses %j', (value) => {
    expect(isDomain(value)).toBe(false);
  });
});

describe('fillEnvTemplate', () => {
  it('fills a variable in place, also where the template comments it out', () => {
    const filled = fillEnvTemplate(TEMPLATE, { BASE_DOMAIN: 'example.com', WATCHTOWER_API_TOKEN: 'token' });

    expect(filled.split('\n')).toEqual([
      '# Required',
      'BASE_DOMAIN=example.com',
      'INSTANCE_KEY=',
      'POSTGRES_PASSWORD=',
      'POSTGRES_USERNAME=nocturne',
      'WATCHTOWER_API_TOKEN=token',
      '# DISCORD_BOT_TOKEN=',
    ]);
  });
});

describe('InstallGuide', () => {
  it('has no keys until it starts in the browser', () => {
    const guide = new InstallGuide({ id: 'test', envTemplate: TEMPLATE });

    expect(guide.secrets).toBeNull();
    expect(guide.envFile).toBeNull();
  });

  it('makes a different value for each key, and a file name with the date', () => {
    const guide = new InstallGuide({ id: 'test', envTemplate: TEMPLATE });
    guide.start();

    const secrets = Object.values(guide.secrets ?? {});
    expect(secrets.length).toBeGreaterThan(4);
    expect(new Set(secrets).size).toBe(secrets.length);
    expect(guide.secrets?.INSTANCE_KEY).toMatch(/^[0-9a-f]{64}$/);
    expect(guide.fileName).toMatch(/^nocturne-keys-\d{4}-\d{2}-\d{2}\.env$/);
  });

  it('makes no keys for a guide without a keys file', () => {
    const guide = new InstallGuide({ id: 'test' });
    guide.start();

    expect(guide.secrets).toBeNull();
    expect(guide.envFile).toBeNull();
  });

  it('gives a host only for a valid domain', () => {
    const guide = new InstallGuide({ id: 'test' });

    guide.setDomain('  example.com ');
    expect(guide.host).toBe('example.com');

    guide.setDomain('https://example.com');
    expect(guide.domainValid).toBe(false);
    expect(guide.host).toBeNull();
  });

  it('puts the domain and the keys in the file', () => {
    const guide = new InstallGuide({ id: 'test', envTemplate: TEMPLATE });
    guide.start();
    guide.setDomain('example.com');

    expect(guide.envFile).toContain('BASE_DOMAIN=example.com');
    expect(guide.envFile).toContain(`INSTANCE_KEY=${guide.secrets?.INSTANCE_KEY}`);
  });

  it('remembers a valid domain for the other guides, and never an invalid one or the keys', () => {
    const first = new InstallGuide({ id: 'portainer', envTemplate: TEMPLATE });
    first.start();
    first.setDomain('example.com');
    first.setDomain('example.com/oops');

    const second = new InstallGuide({ id: 'docker-compose', envTemplate: TEMPLATE });
    second.start();

    expect(second.domainInput).toBe('example.com');
    expect(second.secrets?.INSTANCE_KEY).not.toBe(first.secrets?.INSTANCE_KEY);
    expect(localStorage.length).toBe(1);
    expect(localStorage.getItem('nocturne-docs:install-domain')).toBe('example.com');
  });

  it('keeps a choice for its own guide only', () => {
    const portainer = new InstallGuide({ id: 'portainer' });
    expect(portainer.choice('deploy', 'template')).toBe('template');
    portainer.choose('deploy', 'editor');

    const reloaded = new InstallGuide({ id: 'portainer' });
    reloaded.start();
    const other = new InstallGuide({ id: 'docker-compose' });
    other.start();

    expect(reloaded.choice('deploy', 'template')).toBe('editor');
    expect(other.choice('deploy', 'template')).toBe('template');
  });

  it('ignores stored choices that are not text', () => {
    localStorage.setItem('nocturne-docs:install-choices:portainer', '{"deploy":3,"other":"kept"}');
    const guide = new InstallGuide({ id: 'portainer' });
    guide.start();

    expect(guide.choice('deploy', 'template')).toBe('template');
    expect(guide.choice('other', 'fallback')).toBe('kept');
  });

  it('ignores stored choices that are not JSON', () => {
    localStorage.setItem('nocturne-docs:install-choices:portainer', 'not json');
    const guide = new InstallGuide({ id: 'portainer' });

    expect(() => guide.start()).not.toThrow();
    expect(guide.choice('deploy', 'template')).toBe('template');
  });

  it('works when the browser blocks storage', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    });
    const guide = new InstallGuide({ id: 'portainer', envTemplate: TEMPLATE });

    expect(() => guide.start()).not.toThrow();
    expect(() => guide.setDomain('example.com')).not.toThrow();
    expect(() => guide.choose('deploy', 'editor')).not.toThrow();
    expect(guide.choice('deploy', 'template')).toBe('editor');
  });

  describe('the download gate', () => {
    function downloadable(): InstallGuide {
      const click = vi.fn();
      vi.stubGlobal('document', { createElement: () => ({ click, href: '', download: '' }) });
      const guide = new InstallGuide({ id: 'test', envTemplate: TEMPLATE });
      guide.start();
      return guide;
    }

    it('does not download without a valid domain', () => {
      const guide = downloadable();
      guide.download();

      expect(guide.downloadedFile).toBeNull();
      expect(guide.keysReady).toBe(false);
    });

    it('hands out the keys only after the download and the confirmation', () => {
      const guide = downloadable();
      guide.setDomain('example.com');

      guide.download();
      expect(guide.downloadCurrent).toBe(true);
      expect(guide.keysReady).toBe(false);

      guide.saved = true;
      expect(guide.keysReady).toBe(true);
    });

    it('needs a new download and a new confirmation after the domain changes', () => {
      const guide = downloadable();
      guide.setDomain('example.com');
      guide.download();
      guide.saved = true;

      guide.setDomain('other.example.com');
      expect(guide.downloadCurrent).toBe(false);
      expect(guide.keysReady).toBe(false);

      guide.download();
      expect(guide.saved).toBe(false);
      expect(guide.keysReady).toBe(false);
    });
  });

  it('sends the reader to the domain field', () => {
    const guide = new InstallGuide({ id: 'test' });
    const focus = vi.fn();
    guide.domainField = { focus } as unknown as HTMLInputElement;

    guide.focusDomain();

    expect(focus).toHaveBeenCalledOnce();
  });
});
