<script lang="ts">
  import ContentEditor from '@nocturne/cms/editor/ContentEditor.svelte';
  import { blogMetadataFields } from '@nocturne/cms/editor/types';
  import { toSvx } from '@nocturne/cms/editor/markdown';
  import type { ContentTypeConfig, EditorCallbacks, ContentItem, ContentData } from '@nocturne/cms/editor/types';
  import type { ComponentDefinition } from '@nocturne/cms/editor/extensions/svelte-component';
  import LanguageSelector from '$lib/components/LanguageSelector.svelte';

  const portalComponents: ComponentDefinition[] = [
    {
      name: 'LanguageSelector',
      label: 'Language Selector',
      importPath: '$lib/components/LanguageSelector.svelte',
      defaultProps: { compact: 'true' },
    },
  ];

  const previewComponents: Record<string, typeof LanguageSelector> = {
    LanguageSelector,
  };

  const STORAGE_KEY = 'nocturne-studio-blog';
  const CONTRIBUTOR_KEY = 'nocturne-studio-contributor';

  interface Proposal {
    slug: string;
    title: string;
    content: string;
    resolve: () => void;
  }

  let proposal = $state<Proposal | null>(null);
  let proposing = $state(false);
  let proposeError = $state<string | null>(null);
  let proposedPr = $state<{ url: string; number: number } | null>(null);
  let contributorName = $state('');
  let contributorGitHub = $state('');
  let contributorEmail = $state('');
  let proposalNote = $state('');

  $effect(() => {
    try {
      const stored = JSON.parse(localStorage.getItem(CONTRIBUTOR_KEY) || '{}');
      contributorName = stored.name ?? '';
      contributorGitHub = stored.gitHubUsername ?? '';
      contributorEmail = stored.email ?? '';
    } catch {
      // Ignore malformed stored contributor info.
    }
  });

  async function submitProposal() {
    if (!proposal) return;
    proposing = true;
    proposeError = null;
    try {
      localStorage.setItem(
        CONTRIBUTOR_KEY,
        JSON.stringify({
          name: contributorName,
          gitHubUsername: contributorGitHub,
          email: contributorEmail,
        }),
      );
      const res = await fetch('/studio/propose', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          slug: proposal.slug,
          title: proposal.title,
          content: proposal.content,
          contributor: {
            name: contributorName,
            gitHubUsername: contributorGitHub || null,
            email: contributorEmail || null,
          },
          note: proposalNote || null,
        }),
      });
      if (!res.ok) {
        const detail = await res.text().catch(() => '');
        throw new Error(detail || 'Failed to propose the change');
      }
      const result = await res.json();
      proposedPr = { url: result.prUrl ?? '', number: result.prNumber ?? 0 };
      proposal.resolve();
      proposal = null;
    } catch (e) {
      proposeError = e instanceof Error ? e.message : 'Failed to propose the change';
    } finally {
      proposing = false;
    }
  }

  function cancelProposal() {
    // Cancelling is a normal outcome (the local draft is kept), so resolve
    // rather than reject: ContentEditor awaits publish without a catch and a
    // rejection would surface as an unhandled promise rejection.
    proposal?.resolve();
    proposal = null;
    proposeError = null;
  }

  function getStorage(): Record<string, ContentData> {
    try {
      return JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}');
    } catch {
      return {};
    }
  }

  function setStorage(data: Record<string, ContentData>) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
  }

  /** Fetch published .svx files from the filesystem */
  async function fetchFilesystemPosts(): Promise<Array<{ slug: string; content: string; metadata: Record<string, unknown> }>> {
    try {
      const res = await fetch('/studio/content');
      if (!res.ok) return [];
      return await res.json();
    } catch {
      return [];
    }
  }

  const config: ContentTypeConfig = {
    mode: 'blog',
    label: 'Blog Posts',
    metadataFields: blogMetadataFields,
    preview: 'markdown',
  };

  const callbacks: EditorCallbacks = {
    async list(): Promise<ContentItem[]> {
      const [fsPosts, storage] = await Promise.all([
        fetchFilesystemPosts(),
        Promise.resolve(getStorage()),
      ]);

      // Filesystem posts (published, on disk)
      const fsItems: ContentItem[] = fsPosts.map((post) => ({
        id: post.slug,
        title: String(post.metadata.title || post.slug),
        status: 'published' as const,
        updatedAt: String(post.metadata.date || ''),
        metadata: post.metadata,
      }));

      // localStorage drafts that aren't already on disk
      const fsSlugs = new Set(fsPosts.map((p) => p.slug));
      const draftItems: ContentItem[] = Object.entries(storage)
        .filter(([id]) => !fsSlugs.has(id) && !fsSlugs.has(String(storage[id].metadata.slug)))
        .map(([id, data]) => ({
          id,
          title: String(data.metadata.title || 'Untitled'),
          status: 'draft' as const,
          updatedAt: String(data.metadata.date || ''),
          metadata: data.metadata,
        }));

      return [...fsItems, ...draftItems];
    },

    async load(id: string): Promise<ContentData> {
      // Check localStorage first (may have unsaved edits)
      const storage = getStorage();
      if (storage[id]) {
        return storage[id];
      }

      // Fall back to filesystem
      const fsPosts = await fetchFilesystemPosts();
      const post = fsPosts.find((p) => p.slug === id);
      if (post) {
        return { id: post.slug, content: post.content, metadata: post.metadata };
      }

      return { id, content: '', metadata: {} };
    },

    async save(id: string, content: string, metadata: Record<string, unknown>) {
      const storage = getStorage();
      storage[id] = { id, content, metadata };
      setStorage(storage);
    },

    async publish(id: string) {
      // "Publish" proposes the draft to the Nocturne repo as a pull request
      // through the content-contribution relay. The local draft is kept: the
      // published file only changes once the PR merges.
      const storage = getStorage();
      const item = storage[id];
      if (!item) return;

      const slug = String(item.metadata.slug || id);
      const title = String(item.metadata.title || slug);
      const svxContent = toSvx(item.metadata, item.content);

      // Settle any dialog already open so its awaiting publish call cannot
      // leak as a forever-pending promise.
      proposal?.resolve();
      await new Promise<void>((resolve) => {
        proposal = { slug, title, content: svxContent, resolve };
      });
    },

    async create(metadata: Record<string, unknown>): Promise<string> {
      const id = crypto.randomUUID();
      const storage = getStorage();
      storage[id] = { id, content: '', metadata };
      setStorage(storage);
      return id;
    },

    async delete(id: string) {
      const storage = getStorage();
      delete storage[id];
      setStorage(storage);
    },
  };
</script>

<svelte:head>
  <title>Studio - Nocturne</title>
</svelte:head>

{#if proposedPr}
  <div class="flex items-center justify-between gap-3 border-b border-border bg-muted/50 px-4 py-2 text-sm">
    <span>
      Proposed as pull request
      {#if proposedPr.url}
        <a
          href={proposedPr.url}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary underline underline-offset-4"
        >
          #{proposedPr.number}
        </a>
      {/if}
      — the post updates once the pull request merges.
    </span>
    <button
      type="button"
      class="text-muted-foreground hover:text-foreground"
      onclick={() => (proposedPr = null)}
    >
      Dismiss
    </button>
  </div>
{/if}

<ContentEditor {config} {callbacks} components={portalComponents} previewComponentMap={previewComponents} />

{#if proposal}
  <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
    <div class="w-full max-w-lg space-y-4 rounded-lg border border-border bg-background p-6 shadow-lg">
      <div>
        <h2 class="text-lg font-semibold">Propose as pull request</h2>
        <p class="mt-1 text-sm text-muted-foreground">
          "{proposal.title}" is proposed to the Nocturne project as a pull
          request. Your name appears in the commit credit.
        </p>
      </div>
      <div class="space-y-3">
        <label class="block space-y-1 text-sm">
          <span>Name</span>
          <input
            class="w-full rounded-md border border-input bg-background px-3 py-2"
            bind:value={contributorName}
            placeholder="Your name"
          />
        </label>
        <label class="block space-y-1 text-sm">
          <span>GitHub username (optional, used for commit co-author credit)</span>
          <input
            class="w-full rounded-md border border-input bg-background px-3 py-2"
            bind:value={contributorGitHub}
            placeholder="octocat"
          />
        </label>
        <label class="block space-y-1 text-sm">
          <span>Email (optional)</span>
          <input
            type="email"
            class="w-full rounded-md border border-input bg-background px-3 py-2"
            bind:value={contributorEmail}
          />
        </label>
        <label class="block space-y-1 text-sm">
          <span>Note to reviewers (optional)</span>
          <textarea
            class="w-full rounded-md border border-input bg-background px-3 py-2"
            rows="3"
            bind:value={proposalNote}
          ></textarea>
        </label>
        {#if proposeError}
          <p class="text-sm text-destructive">{proposeError}</p>
        {/if}
      </div>
      <div class="flex justify-end gap-2">
        <button
          type="button"
          class="rounded-md border border-input px-4 py-2 text-sm"
          onclick={cancelProposal}
          disabled={proposing}
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground disabled:opacity-50"
          onclick={submitProposal}
          disabled={proposing || contributorName.trim().length === 0}
        >
          {proposing ? 'Proposing…' : 'Propose'}
        </button>
      </div>
    </div>
  </div>
{/if}
