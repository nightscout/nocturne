<script lang="ts" module>
  export function initialsOf(name: string | null | undefined): string {
    const initials = (name ?? "")
      .split(/\s+/)
      .map((part) => part[0])
      .filter(Boolean)
      .slice(0, 2)
      .join("")
      .toUpperCase();
    return initials || "?";
  }
</script>

<script lang="ts">
  import * as Avatar from "$lib/components/ui/avatar";
  import { AvatarWash } from "@nocturne/watercolour";

  interface Props {
    name: string | null | undefined;
    src?: string | null;
    size?: "sm" | "md" | "lg";
  }

  let { name, src, size = "sm" }: Props = $props();

  const WASH_PX = { sm: 48, md: 60, lg: 96 } as const;

  const initials = $derived(initialsOf(name));

  let status = $state<"loading" | "loaded" | "error">("loading");
  // A wash behind a loaded photo would be simulated for nothing.
  const showWash = $derived(!src || status === "error");
</script>

<Avatar.Root {size} onLoadingStatusChange={(next) => (status = next)}>
  <Avatar.Image src={src ?? undefined} alt={name ?? ""} />
  <Avatar.Fallback variant="wash">
    <!-- The painted disc covers about two thirds of its square, so the wash is
         drawn at 1.5x the avatar and clipped to the circle. Half opacity keeps
         foreground initials above 4.5:1 on either theme's wash. -->
    {#if showWash}
      <span class="absolute inset-0 flex items-center justify-center opacity-50">
        <AvatarWash name={name || initials} size={WASH_PX[size]} />
      </span>
    {/if}
    <span class="relative">{initials}</span>
  </Avatar.Fallback>
</Avatar.Root>
