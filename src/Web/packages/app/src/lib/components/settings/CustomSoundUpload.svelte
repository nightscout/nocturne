<script lang="ts">
  import { onMount } from "svelte";
  import {
    uploadCustomSound,
    deleteCustomSound,
    getCustomSounds,
    previewAlarmSound,
    stopPreview,
    CustomSoundRejectedError,
    CUSTOM_SOUND_ACCEPT,
    MAX_CUSTOM_SOUND_SIZE_MB,
    type CustomAlarmSound,
    type CustomSoundRejection,
  } from "#lib/audio/alarm-sounds.js";
  import { formatFileSize } from "#lib/utils/formatting.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { soundArtwork } from "#lib/watercolour-icons.js";
  import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
    DialogTrigger,
  } from "#lib/components/ui/dialog/index.js";
  import Upload from "@lucide/svelte/icons/upload";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Play from "@lucide/svelte/icons/play";
  import Square from "@lucide/svelte/icons/square";
  import FileAudio from "@lucide/svelte/icons/file-headphone";
  import AlertCircle from "@lucide/svelte/icons/circle-alert";
  import Check from "@lucide/svelte/icons/check";
  import Loader2 from "@lucide/svelte/icons/loader-circle";

  interface Props {
    onSoundSelected?: (soundId: string) => void;
    selectedSoundId?: string;
  }

  let { onSoundSelected, selectedSoundId }: Props = $props();

  let customSounds = $state<CustomAlarmSound[]>([]);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let isUploading = $state(false);
  let uploadDialogOpen = $state(false);
  let previewingId = $state<string | null>(null);

  // Upload form state
  let fileInput: HTMLInputElement | null = $state(null);
  let selectedFile = $state<File | null>(null);
  let customName = $state("");

  onMount(async () => {
    await loadSounds();
  });

  async function loadSounds() {
    isLoading = true;
    error = null;
    try {
      customSounds = await getCustomSounds();
    } catch (err) {
      error =
        err instanceof Error ? err.message : "Failed to load custom sounds";
    } finally {
      isLoading = false;
    }
  }

  function handleFileSelect(event: Event) {
    const input = event.target;
    if (!(input instanceof HTMLInputElement)) return;
    const file = input.files?.[0];
    if (file) {
      selectedFile = file;
      // Use filename without extension as default name
      customName = file.name.replace(/\.[^.]+$/, "");
    }
  }

  // @wc-ignore
  const FORMATS = "MP3, WAV, OGG, FLAC, WebM, M4A, AAC";

  function describeRejection(reason: CustomSoundRejection): string {
    switch (reason) {
      case "unsupported-type":
        return `Choose one of these formats: ${FORMATS}`;
      case "too-large":
        return `The file is larger than ${MAX_CUSTOM_SOUND_SIZE_MB} MB`;
      case "undecodable":
        return "This file could not be played. Check that it is a valid audio file.";
      case "timed-out":
        return "The audio file took too long to load";
    }
  }

  async function handleUpload() {
    if (!selectedFile) return;

    isUploading = true;
    error = null;

    try {
      const sound = await uploadCustomSound(
        selectedFile,
        customName || undefined
      );
      customSounds = [...customSounds, sound];

      // Reset form
      selectedFile = null;
      customName = "";
      if (fileInput) fileInput.value = "";
      uploadDialogOpen = false;

      // Select the newly uploaded sound
      onSoundSelected?.(sound.id);
    } catch (err) {
      error =
        err instanceof CustomSoundRejectedError
          ? describeRejection(err.reason)
          : err instanceof Error
            ? err.message
            : "Failed to upload sound";
    } finally {
      isUploading = false;
    }
  }

  async function handleDelete(sound: CustomAlarmSound) {
    if (!confirm(`Delete "${sound.name}"? This cannot be undone.`)) {
      return;
    }

    try {
      await deleteCustomSound(sound.id);
      customSounds = customSounds.filter((s) => s.id !== sound.id);

      // If this was the selected sound, clear selection
      if (selectedSoundId === sound.id) {
        onSoundSelected?.("alarm-default");
      }
    } catch (err) {
      error = err instanceof Error ? err.message : "Failed to delete sound";
    }
  }

  async function handlePreview(sound: CustomAlarmSound) {
    if (previewingId === sound.id) {
      stopPreview();
      previewingId = null;
      return;
    }

    previewingId = sound.id;
    try {
      await previewAlarmSound(sound.id, { volume: 80 });
    } finally {
      previewingId = null;
    }
  }
</script>

<div class="space-y-4">
  <div class="flex flex-wrap items-center justify-between gap-3">
    <div>
      <Label size="lg">Custom sounds</Label>
      <p class="text-sm text-muted-foreground">
        Upload your own alarm sounds ({FORMATS}, up to {MAX_CUSTOM_SOUND_SIZE_MB} MB)
      </p>
    </div>

    <Dialog bind:open={uploadDialogOpen}>
      <DialogTrigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button variant="outline" size="sm" {...props}>
            <Upload class="h-4 w-4 mr-2" />
            Upload sound
          </Button>
        {/snippet}
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Upload custom sound</DialogTitle>
          <DialogDescription>
            Upload an audio file to use as a custom alarm sound.
          </DialogDescription>
        </DialogHeader>

        <div class="space-y-4 py-4">
          {#if error}
            <div
              class="flex items-center gap-2 p-3 rounded-lg bg-destructive/10 text-destructive text-sm"
            >
              <AlertCircle class="h-4 w-4 shrink-0" />
              <span>{error}</span>
            </div>
          {/if}

          <div class="space-y-2">
            <Label for="audio-file">Audio file</Label>
            <div class="flex items-center gap-2">
              <Input
                id="audio-file"
                type="file"
                accept={CUSTOM_SOUND_ACCEPT}
                onchange={handleFileSelect}
                bind:ref={fileInput}
                class="flex-1"
              />
            </div>
            {#if selectedFile}
              <p class="text-sm text-muted-foreground flex items-center gap-2">
                <FileAudio class="h-4 w-4" />
                {selectedFile.name} ({formatFileSize(selectedFile.size)})
              </p>
            {/if}
          </div>

          <div class="space-y-2">
            <Label for="sound-name">Display name</Label>
            <Input
              id="sound-name"
              type="text"
              placeholder="My custom alarm"
              bind:value={customName}
            />
            <p class="text-xs text-muted-foreground">
              Optional: Give your sound a friendly name
            </p>
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onclick={() => (uploadDialogOpen = false)}>
            Cancel
          </Button>
          <Button
            onclick={handleUpload}
            disabled={!selectedFile || isUploading}
          >
            {#if isUploading}
              <Loader2 class="h-4 w-4 mr-2 animate-spin" />
              Uploading...
            {:else}
              <Upload class="h-4 w-4 mr-2" />
              Upload
            {/if}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>

  {#if isLoading}
    <div class="flex items-center justify-center py-8 text-muted-foreground">
      <Loader2 class="h-5 w-5 animate-spin mr-2" />
      Loading custom sounds...
    </div>
  {:else if customSounds.length === 0}
    <EmptyState
      art={soundArtwork}
      size="compact"
      variant="outline"
      title="No custom sounds uploaded yet"
      body='Select "Upload sound" to add your own'
    />
  {:else}
    <div class="space-y-2">
      {#each customSounds as sound (sound.id)}
        <div
          class="flex items-center gap-3 p-3 rounded-lg border transition-colors
            {selectedSoundId === sound.id
            ? 'bg-primary/5 border-primary'
            : 'bg-muted/30 hover:bg-muted/50'}"
        >
          <!-- eslint-disable-next-line no-restricted-syntax -- row region selecting the sound -->
          <button
            type="button"
            class="flex-1 flex items-center gap-3 text-left"
            onclick={() => onSoundSelected?.(sound.id)}
          >
            <div
              class="flex items-center justify-center w-10 h-10 rounded-lg bg-primary/10"
            >
              <FileAudio class="h-5 w-5 text-primary" />
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2">
                <span class="font-medium truncate">{sound.name}</span>
                {#if selectedSoundId === sound.id}
                  <Check class="h-4 w-4 text-primary shrink-0" />
                {/if}
              </div>
              <div class="text-xs text-muted-foreground truncate">
                {sound.fileName} • {formatFileSize(sound.size)}
              </div>
            </div>
          </button>

          <div class="flex items-center gap-1">
            <Button
              variant={previewingId === sound.id ? "default" : "ghost"}
              size="icon"
              onclick={() => handlePreview(sound)}
              title={previewingId === sound.id ? "Stop" : "Preview"}
            >
              {#if previewingId === sound.id}
                <Square class="h-4 w-4 fill-current" />
              {:else}
                <Play class="h-4 w-4" />
              {/if}
            </Button>
            <Button
              variant="ghost-destructive"
              size="icon"
              onclick={() => handleDelete(sound)}
              title="Delete"
            >
              <Trash2 class="h-4 w-4" />
            </Button>
          </div>
        </div>
      {/each}
    </div>
  {/if}
</div>
