<script lang="ts">
    import Copy from "@lucide/svelte/icons/copy";
    import Check from "@lucide/svelte/icons/check";
    import { Button } from "@nocturne/ui/ui/button";
    import { copyToClipboard } from "@nocturne/ui/utils";
    import { installStepBefore, track } from "#lib/analytics.js";
    import { getInstallGuide } from "./guide.svelte.js";

    interface Props {
        /** The id of the step that makes the file, for the link shown while the file is not ready. */
        keysStep: string;
    }

    let { keysStep }: Props = $props();

    const guide = getInstallGuide();

    let copyState = $state<"idle" | "copied" | "failed">("idle");

    async function copy(event: MouseEvent) {
        if (!guide?.envFile || !guide.keysReady) return;
        // Read before the await: the handler's event no longer has a currentTarget after it.
        const button = event.currentTarget instanceof Element ? event.currentTarget : undefined;
        if (!(await copyToClipboard(guide.envFile))) {
            copyState = "failed";
            return;
        }
        copyState = "copied";
        const step = button && installStepBefore(button);
        track("Docs Copy", step ? { kind: "password", step } : { kind: "password" });
        setTimeout(() => (copyState = "idle"), 2000);
    }
</script>

{#if guide}
    <div class="not-prose mb-4 flex flex-wrap items-center gap-3">
        <Button onclick={copy} disabled={!guide.keysReady}>
            {#if copyState === "copied"}
                <Check class="size-4" />
                Copied
            {:else}
                <Copy class="size-4" />
                Copy the file
            {/if}
        </Button>
        {#if copyState === "failed"}
            <p class="m-0 text-sm text-destructive">
                Could not copy the file. Open the file that you downloaded and copy its text.
            </p>
        {:else if !guide.keysReady}
            <p class="m-0 text-sm text-muted-foreground">
                <a href="#{keysStep}" class="text-primary hover:underline">Download and save the file</a>
                first. Then you can copy it.
            </p>
        {/if}
    </div>
{/if}
