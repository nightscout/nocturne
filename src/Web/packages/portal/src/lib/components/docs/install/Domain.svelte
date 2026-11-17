<script lang="ts">
    import { getInstallGuide } from "./guide.svelte.js";

    interface Props {
        /** Text in front of the domain, e.g. a wildcard or a subdomain. */
        prefix?: string;
        suffix?: string;
        /** Shown until the reader enters a domain. */
        fallback?: string;
    }

    let { prefix = "", suffix = "", fallback = "example.com" }: Props = $props();

    const guide = getInstallGuide();
</script>

<code
    class="text-xs bg-muted/50 px-1.5 py-0.5 rounded {guide?.host ? 'text-foreground font-medium' : ''}"
    >{prefix}{guide?.host ?? fallback}{suffix}</code
>
