<script lang="ts">
  import type { Snippet } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import {
    ArrowLeft,
    Play,
    Save,
    Loader2,
    Copy,
    Undo2,
    Redo2,
  } from "lucide-svelte";

  interface Props {
    clockName: string;
    saving: boolean;
    canUndo: boolean;
    canRedo: boolean;
    onNameChange: (name: string) => void;
    onUndo: () => void;
    onRedo: () => void;
    onCopyLink: () => void;
    onSave: () => void;
    onPreview: () => void;
    children?: Snippet;
  }

  let {
    clockName,
    saving,
    canUndo,
    canRedo,
    onNameChange,
    onUndo,
    onRedo,
    onCopyLink,
    onSave,
    onPreview,
    children,
  }: Props = $props();
</script>

<header class="flex items-center justify-between border-b px-4 py-3">
  <Button variant="ghost" href="/clock" class="gap-2">
    <ArrowLeft class="size-4" />
    Back
  </Button>
  <Input
    type="text"
    value={clockName}
    oninput={(e) => onNameChange(e.currentTarget.value)}
    class="max-w-[200px] border-none bg-transparent text-center font-semibold focus-visible:ring-0"
    placeholder="Clock name"
  />
  <div class="flex gap-2">
    <Button
      variant="outline"
      size="icon"
      onclick={onUndo}
      disabled={!canUndo}
      title="Undo (Ctrl+Z)"
    >
      <Undo2 class="size-4" />
    </Button>
    <Button
      variant="outline"
      size="icon"
      onclick={onRedo}
      disabled={!canRedo}
      title="Redo (Ctrl+Shift+Z)"
    >
      <Redo2 class="size-4" />
    </Button>
    <Button variant="outline" size="icon" onclick={onCopyLink}>
      <Copy class="size-4" />
    </Button>
    {#if children}
      {@render children()}
    {/if}
    <Button variant="outline" onclick={onSave} disabled={saving}>
      {#if saving}<Loader2 class="size-4 animate-spin" />{:else}<Save
          class="size-4"
        />{/if}
    </Button>
    <Button onclick={onPreview}>
      <Play class="size-4" />
      <span class="ml-2 hidden sm:inline">Preview</span>
    </Button>
  </div>
</header>
