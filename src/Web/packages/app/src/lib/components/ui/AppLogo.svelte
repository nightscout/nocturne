<script lang="ts">
  interface Props {
    /**
     * Icon identifier string (e.g., "dexcom", "xdrip", "loop") or filename with
     * extension (e.g., "mylogo.png")
     */
    icon: string | undefined;
    /** CSS class applied to the <img> element */
    class?: string;
    /**
     * When true, swap the dark/light logo variants so that the light logo
     * shows in light mode and the dark logo shows in dark mode (opposite of
     * the default sidebar behaviour).
     */
    invertMode?: boolean;
  }

  const { icon, class: className = "h-full w-full", invertMode = false }: Props = $props();

  const logoExtensions: Record<string, string> = {
    aaps: "png",
    dexcom: "png",
    discord: "png",
    eversense: "png",
    github: "png",
    glooko: "png",
    glucotracker: "png",
    "google-chat": "png",
    "home-assistant": "png",
    juggluco: "png",
    libre: "png",
    loop: "png",
    mylife: "png",
    nightscout: "png",
    nocturne: "png",
    omnipod: "png",
    slack: "png",
    spike: "png",
    sugarmate: "png",
    tandem: "png",
    teams: "png",
    telegram: "png",
    wechat: "png",
    whatsapp: "png",
    imessage: "jpg",
    medtronic: "jpg",
    messenger: "jpg",
    myfitnesspal: "jpg",
    tidepool: "jpg",
    trio: "jpg",
    twiist: "png",
    xdrip: "jpg",
    xdrip4ios: "jpg",
  };

  const hasDarkVariant = $derived((icon ?? "device") === "nocturne");

  const src = $derived.by(() => {
    const name = icon ?? "device";
    if (name.includes(".")) return `/logos/${name}`;
    const ext = logoExtensions[name] ?? "svg";
    return `/logos/${name}.${ext}`;
  });

  const lightSrc = $derived(hasDarkVariant ? "/logos/nocturne-light.png" : null);
</script>

{#if hasDarkVariant && lightSrc}
  <!-- Dark variant (nocturne.png = light logo for dark backgrounds) -->
  <img
    src={invertMode ? lightSrc : src}
    alt=""
    class="object-cover rounded-[inherit] dark:block hidden {className}"
    draggable="false"
  />
  <!-- Light variant (nocturne-light.png = dark logo for light backgrounds) -->
  <img
    src={invertMode ? src : lightSrc}
    alt=""
    class="object-cover rounded-[inherit] dark:hidden block {className}"
    draggable="false"
  />
{:else}
  <img
    {src}
    alt=""
    class="object-cover rounded-[inherit] {className}"
    draggable="false"
  />
{/if}
