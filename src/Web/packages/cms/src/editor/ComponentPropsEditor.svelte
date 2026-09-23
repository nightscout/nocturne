<script lang="ts">
  import { Button } from '@nocturne/ui/ui/button';
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
      {#each Object.entries(selectedComponent.props) as [key, value] (key)}
        <div class="flex items-center gap-2">
          <Label size="sm" class="w-24 shrink-0">{key}</Label>
          {#if value === 'true' || value === 'false'}
            <Switch
              checked={value === 'true'}
              onCheckedChange={(checked) => updateProp(key, String(checked))}
            />
          {:else}
            <Input
              size="xs"
              value={value}
              oninput={(e) => updateProp(key, e.currentTarget.value)}
            />
          {/if}
          <Button variant="subtle" size="icon-2xs" class="shrink-0" onclick={() => removeProp(key)}>
            <X />
          </Button>
        </div>
      {/each}

      <!-- Add new prop -->
      <div class="flex items-center gap-2 pt-1 border-t border-border/20">
        <Input
          size="xs"
          class="w-24 shrink-0"
          placeholder="prop"
          bind:value={newPropKey}
        />
        <Input
          size="xs"
          placeholder="value"
          bind:value={newPropValue}
          onkeydown={(e) => e.key === 'Enter' && addProp()}
        />
        <Button variant="link" size="inline-xs" class="shrink-0" onclick={addProp}>
          Add
        </Button>
      </div>
    </div>
  </div>
{/if}
