export type Spec = {
  paths?: Record<string, Record<string, { tags?: string[] }>>;
  tags?: { name: string; description?: string; 'x-displayName'?: string }[];
  'x-tagGroups'?: { name: string; tags: string[] }[];
  components?: {
    securitySchemes?: Record<string, { flows?: Record<string, { scopes?: Record<string, string> }> }>;
  };
};

export type Scope = { name: string; description: string };

export type Resource = { label: string; href: string; summary: string; operations: number };

const DOCUMENTS = ['nocturne', 'nightscout'];
const cache = new Map<string, Promise<Spec>>();

/**
 * The documents to read, most likely first. Scalar puts the slug of the open document first in
 * the hash (`#nocturne/tag/...`), but clears the hash at the top of a page, so the hash is a
 * hint and the other documents follow.
 */
export function documentsFrom(hash: string): string[] {
  const slug = hash.replace(/^#/, '').split('/')[0];
  return DOCUMENTS.includes(slug) ? [slug, ...DOCUMENTS.filter((document) => document !== slug)] : DOCUMENTS;
}

/**
 * The OpenAPI document, from the host that serves this plugin: the API serves both, so the
 * components describe the same build that the reference shows.
 */
export function loadSpec(document: string, origin: string = import.meta.url): Promise<Spec> {
  const url = new URL(`/openapi/${document}.json`, origin).href;
  let spec = cache.get(url);
  if (!spec) {
    spec = fetch(url).then((response) => {
      if (!response.ok) throw new Error(`${url}: ${response.status}`);
      return response.json() as Promise<Spec>;
    });
    cache.set(url, spec);
    spec.catch(() => cache.delete(url));
  }
  return spec;
}

/** The rows of the first document that has any. */
export async function firstRows<Row>(
  documents: string[],
  read: (spec: Spec, document: string) => Row[],
  load: (document: string) => Promise<Spec> = loadSpec,
): Promise<Row[]> {
  for (const document of documents) {
    const rows = read(await load(document), document);
    if (rows.length > 0) return rows;
  }
  return [];
}

/** The scopes of the OAuth security scheme, each once, in the order the document gives them. */
export function scopesOf(spec: Spec): Scope[] {
  const scopes = new Map<string, string>();
  for (const scheme of Object.values(spec.components?.securitySchemes ?? {}))
    for (const flow of Object.values(scheme.flows ?? {}))
      for (const [name, description] of Object.entries(flow.scopes ?? {}))
        if (!scopes.has(name)) scopes.set(name, description);

  return [...scopes].map(([name, description]) => ({ name, description }));
}

/** The anchor Scalar gives a tag. */
export function tagHref(document: string, tag: string): string {
  const slug = tag
    .toLowerCase()
    .replace(/[^\p{L}\p{N} -]/gu, '')
    .trim()
    .replace(/[ -]+/g, '-');
  return `#${document}/tag/${slug}`;
}

/** The entries of one sidebar group that have operations, with the first sentence of each page. */
export function resourcesOf(spec: Spec, document: string, group: string): Resource[] {
  const counts = new Map<string, number>();
  for (const path of Object.values(spec.paths ?? {}))
    for (const operation of Object.values(path))
      for (const tag of operation?.tags ?? []) counts.set(tag, (counts.get(tag) ?? 0) + 1);

  const tags = new Map((spec.tags ?? []).map((tag) => [tag.name, tag]));
  const members = spec['x-tagGroups']?.find((candidate) => candidate.name === group)?.tags ?? [];

  return members
    .filter((name) => counts.has(name))
    .map((name) => ({
      label: tags.get(name)?.['x-displayName'] ?? name,
      href: tagHref(document, name),
      summary: firstSentence(tags.get(name)?.description ?? ''),
      operations: counts.get(name) ?? 0,
    }));
}

function firstSentence(text: string): string {
  const end = text.search(/\.(\s|$)/);
  return end < 0 ? text : text.slice(0, end + 1);
}
