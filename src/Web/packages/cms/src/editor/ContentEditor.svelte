<script lang="ts">
  import { Button } from '@nocturne/ui/ui/button';
  import { EdraEditor, EdraToolBar, EdraDragHandleExtended } from '../lib/components/edra/shadcn/index.ts';
  import type { Editor } from '@tiptap/core';
  import type { ContentTypeConfig, EditorCallbacks, ContentItem } from './types.ts';
  import { SvelteComponentExtension, type ComponentDefinition } from './extensions/svelte-component.ts';
  import MetadataPanel from './MetadataPanel.svelte';
  import ContentSidebar from './ContentSidebar.svelte';
  import PreviewPane from './PreviewPane.svelte';
  import ComponentPropsEditor from './ComponentPropsEditor.svelte';

  import type { Component } from 'svelte';

  let {
    config,
    callbacks,
    components = [],
    previewComponentMap = {},
  }: {
    config: ContentTypeConfig;
    callbacks: EditorCallbacks;
    components?: ComponentDefinition[];
    /** Map of component names to actual Svelte components for live preview */
    previewComponentMap?: Record<string, Component>;
  } = $props();

  const editorExtensions = components.length > 0 ? [SvelteComponentExtension(components)] : [];

  let editor = $state<Editor>();
  let items = $state<ContentItem[]>([]);
  let selectedId = $state<string>();
  let metadata = $state<Record<string, unknown>>({});
  let previewHtml = $state('');
  let saving = $state(false);

  $effect(() => {
    callbacks.list().then((result) => {
      items = result;
    });
  });

  function handleUpdate() {
    if (!editor) return;
    previewHtml = editor.getHTML();
  }

  async function handleSelect(id: string) {
    selectedId = id;
    const data = await callbacks.load(id);
    metadata = data.metadata;
    if (editor) {
      editor.commands.setContent(data.content);
    }
  }

  async function handleCreate() {
    const defaultMeta: Record<string, unknown> = {};
    for (const field of config.metadataFields) {
      if (field.type === 'date') defaultMeta[field.key] = new Date().toISOString().split('T')[0];
      else if (field.type === 'toggle') defaultMeta[field.key] = false;
      else if (field.type === 'tags') defaultMeta[field.key] = [];
      else defaultMeta[field.key] = '';
    }
    const id = await callbacks.create(defaultMeta);
    selectedId = id;
    metadata = defaultMeta;
    if (editor) editor.commands.clearContent();
    items = await callbacks.list();
  }

  async function handleSave() {
    if (!selectedId || !editor) return;
    saving = true;
    try {
      await callbacks.save(selectedId, editor.getHTML(), metadata);
      items = await callbacks.list();
    } finally {
      saving = false;
    }
  }

  async function handlePublish() {
    if (!selectedId) return;
    await handleSave();
    await callbacks.publish(selectedId);
    items = await callbacks.list();
  }
</script>

<div class="flex min-h-screen bg-background items-start">
  <ContentSidebar
    {items}
    {selectedId}
    onSelect={handleSelect}
    onCreate={handleCreate}
    label={config.label}
  />

  <div class="flex flex-1 flex-col min-w-0 basis-1/2">
    {#if editor}
      <div class="border-b border-border/40">
        <EdraToolBar {editor} />
      </div>
    {/if}

    <MetadataPanel
      fields={config.metadataFields}
      bind:metadata
    />

    <div class="relative flex-1 overflow-y-auto p-4">
      {#if editor}
        <EdraDragHandleExtended {editor} />
      {/if}
      <EdraEditor
        bind:editor
        content=""
        onUpdate={handleUpdate}
        additionalExtensions={editorExtensions}
      />
    </div>

    {#if editor}
      <ComponentPropsEditor {editor} />
    {/if}

    <div class="flex items-center justify-end gap-2 border-t border-border/40 px-4 py-2">
      <span class="mr-auto text-xs text-muted-foreground">
        {#if saving}Saving...{:else if selectedId}Editing{:else}No content selected{/if}
      </span>
      <Button variant="secondary" size="sm" onclick={handleSave} disabled={!selectedId || saving}>
        Save Draft
      </Button>
      <Button size="sm" onclick={handlePublish} disabled={!selectedId}>
        Publish
      </Button>
    </div>
  </div>

  <PreviewPane content={previewHtml} mode={config.preview} componentMap={previewComponentMap} />
</div>
