<script lang="ts">
  import { Input } from '@nocturne/ui/ui/input';
  import { Label } from '@nocturne/ui/ui/label';
  import { Switch } from '@nocturne/ui/ui/switch';
  import { X } from '@lucide/svelte';
  import type { Editor } from '@tiptap/core';
  import { parseComponentProps } from './extensions/svelte-component.ts';

  let {
    editor,
  }: {
    editor: Editor;
  } = $props();

  // Track the selected component node
  const selectedComponent = $derived.by(() => {
    const { selection } = editor.state;
    const node = editor.state.doc.nodeAt(selection.from);
    if (node?.type.name !== 'svelteComponent') return null;
    return {
      pos: selection.from,
      name: String(node.attrs.componentName),
      props: parseComponentProps(node.attrs.props),
    };
  });

  function updateProp(key: string, value: string) {
    if (!selectedComponent) return;
    const newProps = { ...selectedComponent.props, [key]: value };
    // We can't use the command because we need to target a specific position
    const { tr } = editor.state;
    const node = editor.state.doc.nodeAt(selectedComponent.pos);
    if (!node) return;
    tr.setNodeMarkup(selectedComponent.pos, undefined, {
      ...node.attrs,
      props: JSON.stringify(newProps),
    });
    editor.view.dispatch(tr);
  }

  function removeProp(key: string) {
    if (!selectedComponent) return;
    const newProps = { ...selectedComponent.props };
    delete newProps[key];
    const { tr } = editor.state;
    const node = editor.state.doc.nodeAt(selectedComponent.pos);
    if (!node) return;
    tr.setNodeMarkup(selectedComponent.pos, undefined, {
      ...node.attrs,
      props: JSON.stringify(newProps),
    });
    editor.view.dispatch(tr);
  }

  let newPropKey = $state('');
  let newPropValue = $state('');

  function addProp() {
    if (!newPropKey.trim() || !selectedComponent) return;
    updateProp(newPropKey.trim(), newPropValue);
    newPropKey = '';
    newPropValue = '';
  }
</script>

{#if selectedComponent}
  <div class="border-t border-border/40 bg-muted/30 px-4 py-3">
    <div class="flex items-center justify-between mb-2">
      <span class="text-xs font-semibold text-muted-foreground uppercase tracking-wide">
        {selectedComponent.name} Props
      </span>
    </div>

    <div class="space-y-2">
      {#each Object.entries(selectedComponent.props) as [key, value]}
        <div class="flex items-center gap-2">
          <Label class="w-24 shrink-0 text-xs">{key}</Label>
          {#if value === 'true' || value === 'false'}
            <Switch
              checked={value === 'true'}
              onCheckedChange={(checked) => updateProp(key, String(checked))}
            />
          {:else}
            <Input
              class="h-7 text-xs"
              value={value}
              oninput={(e) => updateProp(key, e.currentTarget.value)}
            />
          {/if}
          <button
            class="shrink-0 text-muted-foreground hover:text-destructive"
            onclick={() => removeProp(key)}
          >
            <X class="h-3 w-3" />
          </button>
        </div>
      {/each}

      <!-- Add new prop -->
      <div class="flex items-center gap-2 pt-1 border-t border-border/20">
        <Input
          class="h-7 text-xs w-24 shrink-0"
          placeholder="prop"
          bind:value={newPropKey}
        />
        <Input
          class="h-7 text-xs"
          placeholder="value"
          bind:value={newPropValue}
          onkeydown={(e) => e.key === 'Enter' && addProp()}
        />
        <button
          class="shrink-0 text-xs text-primary hover:text-primary/80 font-medium"
          onclick={addProp}
        >
          Add
        </button>
      </div>
    </div>
  </div>
{/if}
