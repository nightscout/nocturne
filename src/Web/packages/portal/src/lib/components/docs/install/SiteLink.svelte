<script lang="ts">
    import ExternalLink from "@lucide/svelte/icons/external-link";
    import { getInstallGuide } from "./guide.svelte.js";

    interface Props {
        /** A path on the reader's site, with its leading slash. */
        path?: string;
    }

    let { path = "" }: Props = $props();

    const guide = getInstallGuide();
</script>

{#if guide?.host}
    <!-- @wc-ignore -->
    <a
        href="https://{guide.host}{path}"
        target="_blank"
        rel="external noopener noreferrer"
        class="inline-flex items-baseline gap-1 font-medium text-primary hover:underline"
        >https://{guide.host}{path}<ExternalLink class="size-3 self-center" aria-hidden="true" /></a
    >
{:else}
    <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">https://your-domain{path}</code>
{/if}
