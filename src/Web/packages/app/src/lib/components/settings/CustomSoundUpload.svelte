<script lang="ts">
  import { onMount } from "svelte";
  import {
    uploadCustomSound,
    deleteCustomSound,
    getCustomSounds,
    previewAlarmSound,
    stopPreview,
    type CustomAlarmSound,
  } from "$lib/audio/alarm-sounds";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
    DialogTrigger,
  } from "$lib/components/ui/dialog";
  import {
    Upload,
    Trash2,
    Play,
    Square,
    Music,
    FileAudio,
    AlertCircle,
    Check,
    Loader2,
  } from "lucide-svelte";

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
  let fileInput: HTMLInputElement | undefined;
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
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) {
      selectedFile = file;
      // Use filename without extension as default name
      customName = file.name.replace(/\.[^.]+$/, "");
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
      error = err instanceof Error ? err.message : "Failed to upload sound";
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

  function formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
</script>

<div class="space-y-4">
  <div class="flex items-center justify-between">
    <div>
      <Label class="text-base">Custom Sounds</Label>
      <p class="text-sm text-muted-foreground">
        Upload your own alarm sounds (MP3, WAV, OGG, up to 5MB)
      </p>
    </div>

    <Dialog bind:open={uploadDialogOpen}>
      <DialogTrigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button variant="outline" size="sm" {...props}>
            <Upload class="h-4 w-4 mr-2" />
            Upload Sound
          </Button>
        {/snippet}
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Upload Custom Sound</DialogTitle>
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
            <Label for="audio-file">Audio File</Label>
            <div class="flex items-center gap-2">
              <input
                id="audio-file"
                type="file"
                accept="audio/mpeg,audio/wav,audio/ogg,audio/webm,audio/mp4,audio/aac,.mp3,.wav,.ogg,.webm,.m4a,.aac"
                onchange={handleFileSelect}
                bind:this={fileInput}
                class="flex-1 h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
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
            <Label for="sound-name">Display Name</Label>
            <Input
              id="sound-name"
              type="text"
              placeholder="My Custom Alarm"
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
    <div
      class="flex flex-col items-center justify-center py-8 text-muted-foreground border-2 border-dashed rounded-lg"
    >
      <Music class="h-8 w-8 mb-2 opacity-50" />
      <p class="text-sm">No custom sounds uploaded yet</p>
      <p class="text-xs">Click "Upload Sound" to add your own</p>
    </div>
  {:else}
    <div class="space-y-2">
      {#each customSounds as sound (sound.id)}
        <div
          class="flex items-center gap-3 p-3 rounded-lg border transition-colors
            {selectedSoundId === sound.id
            ? 'bg-primary/5 border-primary'
            : 'bg-muted/30 hover:bg-muted/50'}"
        >
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
              class={previewingId === sound.id ? "animate-pulse" : ""}
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
              variant="ghost"
              size="icon"
              class="text-destructive hover:text-destructive"
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
