<script lang="ts">
    interface Props { height?: number; }
    let { height = 340 }: Props = $props();
</script>

<div
    class="rounded-[14px] border border-white/10 bg-[oklch(0.10_0.025_261)] p-6 flex flex-col gap-4 overflow-hidden relative"
    style:height="{height}px"
>
    <!-- Soft glow -->
    <div class="absolute -top-8 -right-8 size-56 rounded-full pointer-events-none"
         style="background: radial-gradient(circle, oklch(0.6 0.118 184.704 / 18%), transparent 60%)"
         aria-hidden="true"></div>

    <!-- Header -->
    <div class="relative">
        <div class="font-mono text-[13px] tracking-[0.06em] uppercase text-glucose-in-range font-bold mb-1.5">
            Sign in to your Nocturne
        </div>
        <div class="text-[14px] text-muted-foreground">No password to forget, leak, or remember.</div>
    </div>

    <!-- Passkey CTA -->
    <button
        type="button"
        class="flex items-center justify-center gap-3 px-4 py-4 rounded-xl bg-[oklch(0.96_0.005_261)] text-[oklch(0.13_0.028_261)] font-semibold text-[16px] border-none cursor-pointer transition-colors hover:bg-white relative"
        tabindex="-1"
        aria-label="Continue with a passkey"
    >
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="oklch(0.13 0.028 261)"
             stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <circle cx="7.5" cy="15.5" r="5.5"/>
            <path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/>
        </svg>
        Continue with a passkey
    </button>

    <!-- Divider -->
    <div class="flex items-center gap-3 font-mono text-[12px] text-muted-foreground/60 tracking-[0.05em] uppercase relative">
        <span class="flex-1 h-px bg-white/10"></span>
        <span>or</span>
        <span class="flex-1 h-px bg-white/10"></span>
    </div>

    <!-- OAuth row -->
    <div class="grid grid-cols-3 gap-2 relative">
        <button type="button" class="oauth-btn" style="background:#fff; color:#1f1f1f; border:1px solid oklch(1 0 0 / 30%)" tabindex="-1">
            <span class="font-bold">G</span> Google
        </button>
        <button type="button" class="oauth-btn" style="background:#1f1f1f; color:#fff; border:1px solid oklch(1 0 0 / 10%)" tabindex="-1">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <path d="M12 2C6.477 2 2 6.477 2 12c0 4.418 2.865 8.167 6.839 9.49.5.092.682-.217.682-.482 0-.237-.009-.866-.013-1.7-2.782.604-3.369-1.342-3.369-1.342-.454-1.155-1.11-1.462-1.11-1.462-.908-.62.069-.608.069-.608 1.003.07 1.531 1.03 1.531 1.03.892 1.529 2.341 1.087 2.91.831.092-.646.35-1.086.636-1.336-2.22-.253-4.555-1.11-4.555-4.943 0-1.091.39-1.984 1.029-2.683-.103-.253-.446-1.27.098-2.647 0 0 .84-.269 2.75 1.025A9.578 9.578 0 0 1 12 6.836c.85.004 1.705.114 2.504.336 1.909-1.294 2.747-1.025 2.747-1.025.546 1.377.202 2.394.1 2.647.64.699 1.028 1.592 1.028 2.683 0 3.842-2.339 4.687-4.566 4.935.359.309.678.919.678 1.852 0 1.336-.012 2.415-.012 2.743 0 .267.18.578.688.48C19.138 20.163 22 16.418 22 12c0-5.523-4.477-10-10-10z"/>
            </svg>
            GitHub
        </button>
        <button type="button" class="oauth-btn" style="background:#000; color:#fff; border:1px solid oklch(1 0 0 / 10%)" tabindex="-1">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <path d="M18.71 19.5c-.83 1.24-1.71 2.45-3.05 2.47-1.34.03-1.77-.79-3.29-.79-1.53 0-2 .77-3.27.82-1.31.05-2.3-1.32-3.14-2.53C4.25 17 2.94 12.45 4.7 9.39c.87-1.52 2.43-2.48 4.12-2.51 1.28-.02 2.5.87 3.29.87.78 0 2.26-1.07 3.8-.91.65.03 2.47.26 3.64 1.98-.09.06-2.17 1.28-2.15 3.81.03 3.02 2.65 4.03 2.68 4.04-.03.07-.42 1.44-1.38 2.83M13 3.5c.73-.83 1.94-1.46 2.94-1.5.13 1.17-.34 2.35-1.04 3.19-.69.85-1.83 1.51-2.95 1.42-.15-1.15.41-2.35 1.05-3.11z"/>
            </svg>
            Apple
        </button>
    </div>

    <!-- Trust bullets -->
    <div class="flex flex-col gap-2.5 mt-auto relative">
        {#each [
            { label: "Nothing leaves your server",    icon: "shield" },
            { label: "Passkeys are device-locked",    icon: "key" },
            { label: "Open source · auditable code",  icon: "check" },
        ] as b (b.label)}
            <div class="flex items-center gap-2.5 text-[13px] text-foreground/80">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="oklch(0.6 0.118 184.704)"
                     stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                    {#if b.icon === "shield"}
                        <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/>
                        <path d="m9 12 2 2 4-4"/>
                    {:else if b.icon === "key"}
                        <circle cx="7.5" cy="15.5" r="5.5"/>
                        <path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/>
                    {:else}
                        <path d="M20 6 9 17l-5-5"/>
                    {/if}
                </svg>
                <span>{b.label}</span>
            </div>
        {/each}
    </div>
</div>

<style>
    .oauth-btn {
        padding: 11px 12px;
        border-radius: 10px;
        font-size: 13px;
        font-weight: 600;
        cursor: default;
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 6px;
        font-family: inherit;
    }
</style>
