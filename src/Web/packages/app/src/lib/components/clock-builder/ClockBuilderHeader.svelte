<script lang="ts">
  import type { Snippet } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { SubmitButton } from "#lib/forms/index.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Play from "@lucide/svelte/icons/play";
  import Save from "@lucide/svelte/icons/save";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Copy from "@lucide/svelte/icons/copy";
  import Undo2 from "@lucide/svelte/icons/undo-2";
  import Redo2 from "@lucide/svelte/icons/redo-2";

  interface Props {
    clockName: string;
    saving: boolean;
    /** See `SubmitButton`'s `saved`. */
    saved: number;
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
    saved,
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

<header
  class="sticky top-(--app-sticky-top,0px) z-30 transition-all duration-300 flex flex-wrap items-center justify-between gap-2 border-b bg-background/95 px-4 py-3 backdrop-blur"
>
  <Button variant="ghost" href="/clock">
    <ArrowLeft class="size-4" />
    Back
  </Button>
  <Input
    type="text"
    value={clockName}
    oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
      onNameChange(e.currentTarget.value)}
    variant="title"
    class="max-w-[200px] text-center @max-xl:order-last @max-xl:max-w-none @max-xl:basis-full"
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
    <Button variant="outline" size="icon" onclick={onCopyLink} title="Copy link">
      <Copy class="size-4" />
    </Button>
    {#if children}
      {@render children()}
    {/if}
    <SubmitButton
      type="button"
      variant="outline"
      {saved}
      onclick={onSave}
      disabled={saving}
      title="Save"
      aria-label="Save"
    >
      {#if saving}<Loader2 class="size-4 animate-spin" />{:else}<Save
          class="size-4"
        />{/if}
    </SubmitButton>
    <Button onclick={onPreview} aria-label="Preview">
      <Play class="size-4" />
      <span class="ml-2 hidden @xl:inline">Preview</span>
    </Button>
  </div>
</header>
