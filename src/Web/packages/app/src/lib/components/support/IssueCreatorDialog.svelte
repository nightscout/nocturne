<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Bug,
    Lightbulb,
    Database,
    CreditCard,
    Upload,
    X,
    ExternalLink,
    Loader2,
    CheckCircle,
    AlertTriangle,
    Eye,
    ArrowLeft,
    Copy,
  } from "lucide-svelte";
  import { createIssue, createOperatorIssue, getFallbackUrl } from "$lib/api/support.remote";

  interface Props {
    open: boolean;
    template: string;
    useOperatorSupport?: boolean;
  }

  let { open = $bindable(false), template = "bug", useOperatorSupport = false }: Props = $props();

  const templateConfigs: Record<
    string,
    { label: string; icon: typeof Bug; description: string }
  > = {
    bug: {
      label: "Bug Report",
      icon: Bug,
      description: "Report something that isn't working correctly",
    },
    feature: {
      label: "Feature Request",
      icon: Lightbulb,
      description: "Suggest a new feature or improvement",
    },
    "data-issue": {
      label: "Data Issue",
      icon: Database,
      description: "Report a problem with CGM data or readings",
    },
    account: {
      label: "Account / Billing",
      icon: CreditCard,
      description: "Get help with your account or billing",
    },
  };

  // Form fields
  let title = $state("");
  let description = $state("");
  let stepsToReproduce = $state("");
  let expectedBehavior = $state("");
  let actualBehavior = $state("");
  let cgmSource = $state("");
  let timeRange = $state("");
  let images = $state<File[]>([]);
  let imagePreviews = $state<string[]>([]);

  // Debug info toggles
  let includeTenantSlug = $state(false);
  let includeCgmSource = $state(false);
  let includeRecentLogs = $state(false);
  let includeSettings = $state(false);

  // UI state
  let formState = $state<"idle" | "preview" | "submitting" | "success" | "error">("idle");
  let issueUrl = $state("");
  let issueNumber = $state(0);
  let isDragging = $state(false);
  let fileInput = $state<HTMLInputElement | null>(null);
  let previewCopied = $state(false);

  const config = $derived(templateConfigs[template] ?? templateConfigs.bug);

  const diagnosticInfo = $derived.by(() => {
    const info: Record<string, unknown> = {
      userAgent:
        typeof navigator !== "undefined" ? navigator.userAgent : "unknown",
      screenSize:
        typeof window !== "undefined"
          ? `${window.innerWidth}x${window.innerHeight}`
          : "unknown",
      route: typeof window !== "undefined" ? window.location.pathname : "unknown",
      locale:
        typeof navigator !== "undefined" ? navigator.language : "unknown",
    };

    if (includeTenantSlug) {
      info.tenantSlug =
        typeof window !== "undefined"
          ? window.location.hostname.split(".")[0]
          : "unknown";
    }
    if (includeCgmSource) {
      info.cgmSource = cgmSource || "not specified";
    }
    if (includeRecentLogs) {
      info.recentLogs = "included";
    }
    if (includeSettings) {
      info.settings = "included";
    }

    return JSON.stringify(info, null, 2);
  });

  const isValid = $derived(
    title.trim().length > 0 &&
      title.length <= 256 &&
      description.trim().length > 0,
  );

  function resetState() {
    title = "";
    description = "";
    stepsToReproduce = "";
    expectedBehavior = "";
    actualBehavior = "";
    cgmSource = "";
    timeRange = "";
    removeAllImages();
    includeTenantSlug = false;
    includeCgmSource = false;
    includeRecentLogs = false;
    includeSettings = false;
    formState = "idle";
    issueUrl = "";
    issueNumber = 0;
    isDragging = false;
  }

  function removeAllImages() {
    for (const url of imagePreviews) {
      URL.revokeObjectURL(url);
    }
    images = [];
    imagePreviews = [];
  }

  function handleClose() {
    resetState();
    open = false;
  }

  function addFiles(files: FileList | File[]) {
    const fileArray = Array.from(files);
    const maxSize = 10 * 1024 * 1024;
    const allowedTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    for (const file of fileArray) {
      if (images.length >= 4) break;
      if (file.size > maxSize) continue;
      if (!allowedTypes.includes(file.type)) continue;

      images = [...images, file];
      imagePreviews = [...imagePreviews, URL.createObjectURL(file)];
    }
  }

  function removeImage(index: number) {
    URL.revokeObjectURL(imagePreviews[index]);
    images = images.filter((_, i) => i !== index);
    imagePreviews = imagePreviews.filter((_, i) => i !== index);
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault();
    isDragging = false;
    if (e.dataTransfer?.files) {
      addFiles(e.dataTransfer.files);
    }
  }

  function handleDragOver(e: DragEvent) {
    e.preventDefault();
    isDragging = true;
  }

  function handleDragLeave() {
    isDragging = false;
  }

  function handleFileInput(e: Event) {
    const input = e.target as HTMLInputElement;
    if (input.files) {
      addFiles(input.files);
      input.value = "";
    }
  }

  function generatePreviewMarkdown(): string {
    const lines: string[] = [];

    lines.push(`# ${title}`);
    lines.push("");
    lines.push("## Description");
    lines.push("");
    lines.push(description);

    if (template === "bug") {
      if (stepsToReproduce.trim()) {
        lines.push("");
        lines.push("## Steps to Reproduce");
        lines.push("");
        lines.push(stepsToReproduce);
      }
      if (expectedBehavior.trim()) {
        lines.push("");
        lines.push("## Expected Behavior");
        lines.push("");
        lines.push(expectedBehavior);
      }
      if (actualBehavior.trim()) {
        lines.push("");
        lines.push("## Actual Behavior");
        lines.push("");
        lines.push(actualBehavior);
      }
    }

    if (template === "data-issue") {
      if (cgmSource.trim()) {
        lines.push("");
        lines.push(`**CGM Source:** ${cgmSource}`);
      }
      if (timeRange.trim()) {
        lines.push("");
        lines.push(`**Time Range:** ${timeRange}`);
      }
    }

    if (images.length > 0) {
      lines.push("");
      lines.push(`**Screenshots:** ${images.length} attached`);
    }

    lines.push("");
    lines.push("<details>");
    lines.push("<summary>Diagnostic Info</summary>");
    lines.push("");
    lines.push("```json");
    lines.push(diagnosticInfo);
    lines.push("```");
    lines.push("");
    lines.push("</details>");

    return lines.join("\n");
  }

  async function copyPreview() {
    try {
      await navigator.clipboard.writeText(generatePreviewMarkdown());
      previewCopied = true;
      setTimeout(() => {
        previewCopied = false;
      }, 2000);
    } catch {
      // Clipboard API may not be available
    }
  }

  async function handleSubmit() {
    if (!isValid || formState === "submitting") return;

    formState = "submitting";

    try {
      const params = {
        template,
        title,
        description,
        stepsToReproduce: template === "bug" ? stepsToReproduce || undefined : undefined,
        expectedBehavior: template === "bug" ? expectedBehavior || undefined : undefined,
        actualBehavior: template === "bug" ? actualBehavior || undefined : undefined,
        cgmSource: template === "data-issue" ? cgmSource || undefined : undefined,
        timeRange: template === "data-issue" ? timeRange || undefined : undefined,
        diagnosticInfo,
        images,
      };

      if (useOperatorSupport) {
        await createOperatorIssue(params);
        formState = "success";
      } else {
        const result = await createIssue(params);
        issueUrl = result.issueUrl;
        issueNumber = result.issueNumber;
        formState = "success";
      }
    } catch {
      formState = "error";
      // Open fallback URL
      try {
        const body = `## Description\n\n${description}`;
        const fallback = await getFallbackUrl({ template, title, body });
        if (fallback?.url) {
          window.open(fallback.url, "_blank");
        }
      } catch {
        // Last resort: open generic issue page
        window.open(
          "https://github.com/nightscout/nocturne/issues/new",
          "_blank",
        );
      }
    }
  }
</script>

<Dialog.Root
  bind:open
  onOpenChange={(isOpen) => {
    if (!isOpen) handleClose();
  }}
>
  <Dialog.Content class="max-w-2xl max-h-[85vh] overflow-hidden flex flex-col">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <config.icon class="h-5 w-5" />
        {config.label}
      </Dialog.Title>
      <Dialog.Description>
        {config.description}
      </Dialog.Description>
    </Dialog.Header>

    {#if formState === "success"}
      <div class="flex flex-col items-center gap-4 py-8">
        <CheckCircle class="h-12 w-12 text-green-500" />
        <h3 class="text-lg font-semibold">Issue Submitted!</h3>
        {#if useOperatorSupport}
          <p class="text-sm text-muted-foreground text-center">
            Your issue has been submitted to the support team.
          </p>
        {:else}
          <p class="text-sm text-muted-foreground text-center">
            Your issue #{issueNumber} has been created successfully.
          </p>
          <a href={issueUrl} target="_blank" rel="noopener noreferrer">
            <Button variant="outline" class="gap-2">
              <ExternalLink class="h-4 w-4" />
              View on GitHub
            </Button>
          </a>
        {/if}
        <Button variant="ghost" onclick={handleClose}>Close</Button>
      </div>
    {:else if formState === "error"}
      <div class="flex flex-col items-center gap-4 py-8">
        <AlertTriangle class="h-12 w-12 text-yellow-500" />
        <h3 class="text-lg font-semibold">Couldn't Create Issue</h3>
        <p class="text-sm text-muted-foreground text-center">
          We've opened a pre-filled GitHub issue form in a new tab as a
          fallback. You can paste your screenshots there manually.
        </p>
        <Button variant="ghost" onclick={handleClose}>Close</Button>
      </div>
    {:else if formState === "idle"}
      <div class="flex-1 overflow-y-auto space-y-4 pr-1">
        <!-- Title -->
        <div class="space-y-2">
          <Label for="issue-title">Title *</Label>
          <Input
            id="issue-title"
            bind:value={title}
            placeholder="Brief summary of the issue"
            maxlength={256}
          />
          <p class="text-xs text-muted-foreground text-right">
            {title.length}/256
          </p>
        </div>

        <!-- Description -->
        <div class="space-y-2">
          <Label for="issue-description">Description *</Label>
          <Textarea
            id="issue-description"
            bind:value={description}
            placeholder={template === "feature"
              ? "Describe the feature and why it would be useful..."
              : "Describe the issue in detail..."}
            rows={4}
          />
        </div>

        <!-- Bug-specific fields -->
        {#if template === "bug"}
          <div class="space-y-2">
            <Label for="issue-steps">Steps to Reproduce</Label>
            <Textarea
              id="issue-steps"
              bind:value={stepsToReproduce}
              placeholder="1. Go to...&#10;2. Click on...&#10;3. Observe..."
              rows={3}
            />
          </div>

          <div class="grid grid-cols-2 gap-4">
            <div class="space-y-2">
              <Label for="issue-expected">Expected Behavior</Label>
              <Textarea
                id="issue-expected"
                bind:value={expectedBehavior}
                placeholder="What should happen..."
                rows={2}
              />
            </div>
            <div class="space-y-2">
              <Label for="issue-actual">Actual Behavior</Label>
              <Textarea
                id="issue-actual"
                bind:value={actualBehavior}
                placeholder="What actually happens..."
                rows={2}
              />
            </div>
          </div>
        {/if}

        <!-- Data issue-specific fields -->
        {#if template === "data-issue"}
          <div class="grid grid-cols-2 gap-4">
            <div class="space-y-2">
              <Label for="issue-cgm">CGM Source</Label>
              <Input
                id="issue-cgm"
                bind:value={cgmSource}
                placeholder="e.g., Dexcom G7, Libre 3..."
              />
            </div>
            <div class="space-y-2">
              <Label for="issue-timerange">Time Range Affected</Label>
              <Input
                id="issue-timerange"
                bind:value={timeRange}
                placeholder="e.g., Last 24 hours, April 25..."
              />
            </div>
          </div>
        {/if}

        <Separator />

        <!-- Image Drop Zone -->
        <div class="space-y-2">
          <Label>Screenshots ({images.length}/4)</Label>
          <button
            type="button"
            class="w-full border-2 border-dashed rounded-lg p-6 text-center transition-colors cursor-pointer {isDragging
              ? 'border-primary bg-primary/5'
              : 'border-muted-foreground/25 hover:border-primary/50'}"
            ondrop={handleDrop}
            ondragover={handleDragOver}
            ondragleave={handleDragLeave}
            onclick={() => fileInput?.click()}
          >
            <Upload class="h-8 w-8 mx-auto mb-2 text-muted-foreground" />
            <p class="text-sm text-muted-foreground">
              Drop images here or click to browse
            </p>
            <p class="text-xs text-muted-foreground mt-1">
              PNG, JPEG, WebP, GIF - max 10 MB each
            </p>
          </button>
          <input
            bind:this={fileInput}
            type="file"
            accept="image/png,image/jpeg,image/webp,image/gif"
            multiple
            class="hidden"
            onchange={handleFileInput}
          />

          {#if imagePreviews.length > 0}
            <div class="flex gap-2 flex-wrap">
              {#each imagePreviews as preview, i (preview)}
                <div class="relative group">
                  <img
                    src={preview}
                    alt="Screenshot {i + 1}"
                    class="h-20 w-20 object-cover rounded-md border"
                  />
                  <button
                    type="button"
                    class="absolute -top-2 -right-2 h-5 w-5 rounded-full bg-destructive text-destructive-foreground flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity"
                    onclick={() => removeImage(i)}
                  >
                    <X class="h-3 w-3" />
                  </button>
                </div>
              {/each}
            </div>
          {/if}
        </div>

        <Separator />

        <!-- Debug Info Toggles -->
        <div class="space-y-3">
          <Label class="text-sm font-medium">Diagnostic Info (included automatically)</Label>
          <p class="text-xs text-muted-foreground">
            Browser, screen size, route, and locale are always included. Toggle
            additional info below:
          </p>

          <div class="space-y-3">
            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label class="text-sm">Tenant slug</Label>
                <p class="text-xs text-muted-foreground">
                  Your instance identifier
                </p>
              </div>
              <Switch bind:checked={includeTenantSlug} />
            </div>

            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label class="text-sm">CGM source</Label>
                <p class="text-xs text-muted-foreground">
                  Your connector type
                </p>
              </div>
              <Switch bind:checked={includeCgmSource} />
            </div>

            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label class="text-sm">Recent logs</Label>
                <p class="text-xs text-muted-foreground">
                  API calls and debug information
                </p>
              </div>
              <Switch bind:checked={includeRecentLogs} />
            </div>

            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label class="text-sm">Settings</Label>
                <p class="text-xs text-muted-foreground">
                  Your configuration (no passwords/tokens)
                </p>
              </div>
              <Switch bind:checked={includeSettings} />
            </div>
          </div>
        </div>
      </div>

      <Dialog.Footer class="pt-4 border-t mt-4">
        <Button variant="outline" onclick={handleClose}>Cancel</Button>
        <Button
          onclick={() => { formState = "preview"; }}
          disabled={!isValid}
        >
          <Eye class="h-4 w-4 mr-2" />
          Preview Issue
        </Button>
      </Dialog.Footer>
    {:else if formState === "preview" || formState === "submitting"}
      <div class="flex-1 overflow-y-auto space-y-4 pr-1">
        <div class="space-y-1">
          <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Title</p>
          <h3 class="text-lg font-semibold">{title}</h3>
        </div>

        <Separator />

        <div class="space-y-1">
          <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Description</p>
          <p class="text-sm whitespace-pre-wrap">{description}</p>
        </div>

        {#if template === "bug"}
          {#if stepsToReproduce.trim()}
            <Separator />
            <div class="space-y-1">
              <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Steps to Reproduce</p>
              <p class="text-sm whitespace-pre-wrap">{stepsToReproduce}</p>
            </div>
          {/if}

          {#if expectedBehavior.trim() || actualBehavior.trim()}
            <Separator />
            <div class="grid grid-cols-2 gap-4">
              {#if expectedBehavior.trim()}
                <div class="space-y-1">
                  <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Expected Behavior</p>
                  <p class="text-sm whitespace-pre-wrap">{expectedBehavior}</p>
                </div>
              {/if}
              {#if actualBehavior.trim()}
                <div class="space-y-1">
                  <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Actual Behavior</p>
                  <p class="text-sm whitespace-pre-wrap">{actualBehavior}</p>
                </div>
              {/if}
            </div>
          {/if}
        {/if}

        {#if template === "data-issue" && (cgmSource.trim() || timeRange.trim())}
          <Separator />
          <div class="grid grid-cols-2 gap-4">
            {#if cgmSource.trim()}
              <div class="space-y-1">
                <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">CGM Source</p>
                <p class="text-sm">{cgmSource}</p>
              </div>
            {/if}
            {#if timeRange.trim()}
              <div class="space-y-1">
                <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Time Range</p>
                <p class="text-sm">{timeRange}</p>
              </div>
            {/if}
          </div>
        {/if}

        {#if imagePreviews.length > 0}
          <Separator />
          <div class="space-y-1">
            <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Screenshots ({images.length})</p>
            <div class="flex gap-2 flex-wrap">
              {#each imagePreviews as preview, i (preview)}
                <img
                  src={preview}
                  alt="Screenshot {i + 1}"
                  class="h-20 w-20 object-cover rounded-md border"
                />
              {/each}
            </div>
          </div>
        {/if}

        <Separator />

        <details class="group">
          <summary class="text-xs font-medium text-muted-foreground uppercase tracking-wide cursor-pointer select-none hover:text-foreground transition-colors">
            Diagnostic Info
          </summary>
          <pre class="mt-2 text-xs bg-muted rounded-md p-3 overflow-x-auto">{diagnosticInfo}</pre>
        </details>
      </div>

      <Dialog.Footer class="pt-4 border-t mt-4">
        <Button variant="outline" onclick={() => { formState = "idle"; }}>
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back
        </Button>
        <div class="flex-1"></div>
        <Button variant="outline" onclick={copyPreview}>
          {#if previewCopied}
            <CheckCircle class="h-4 w-4 mr-2" />
            Copied
          {:else}
            <Copy class="h-4 w-4 mr-2" />
            Copy
          {/if}
        </Button>
        <Button
          onclick={handleSubmit}
          disabled={formState === "submitting"}
        >
          {#if formState === "submitting"}
            <Loader2 class="h-4 w-4 mr-2 animate-spin" />
            Creating Issue...
          {:else}
            Submit Issue
          {/if}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
