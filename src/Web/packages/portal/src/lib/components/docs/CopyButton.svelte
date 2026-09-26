<script lang="ts">
    import { Copy, Check, X } from "@lucide/svelte";
    import { Button } from "@nocturne/ui/ui/button";
    import { copyToClipboard } from "@nocturne/ui/utils";
    import { track } from "$lib/analytics";

    interface Props {
        text: string;
        label?: string;
        /** What was copied. Only the kind is reported, never the text itself. */
        kind?: "code" | "password";
    }

    let { text, label = "Copy to clipboard", kind = "code" }: Props = $props();

    let copied = $state(false);
    let failed = $state(false);
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function copy() {
        if (!(await copyToClipboard(text))) {
            failed = true;
            return;
        }
        failed = false;
        copied = true;
        track("Docs Copy", { kind });
        if (timer !== undefined) clearTimeout(timer);
        timer = setTimeout(() => {
            copied = false;
            timer = undefined;
        }, 2000);
    }
</script>

<Button
    variant="ghost-muted"
    size="icon-xs"
    onclick={copy}
    aria-label={copied ? "Copied" : failed ? "Copy failed. Select the text and copy it manually" : label}
>
    {#if copied}
        <Check class="size-4 text-success" />
    {:else if failed}
        <X class="size-4 text-destructive" />
    {:else}
        <Copy class="size-4" />
    {/if}
</Button>
