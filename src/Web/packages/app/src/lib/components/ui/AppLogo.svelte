<script lang="ts">
  interface Props {
    /**
     * Icon identifier string (e.g., "dexcom", "xdrip", "loop") or filename with
     * extension (e.g., "mylogo.png")
     */
    icon: string | undefined;
    /** CSS class applied to the <img> element */
    class?: string;
  }

  const { icon, class: className = "h-full w-full" }: Props = $props();

  const logoExtensions: Record<string, string> = {
    aaps: "png",
    dexcom: "png",
    discord: "png",
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
    xdrip: "jpg",
    xdrip4ios: "jpg",
  };

  const src = $derived(() => {
    const name = icon ?? "device";
    if (name.includes(".")) return `/logos/${name}`;
    const ext = logoExtensions[name] ?? "svg";
    return `/logos/${name}.${ext}`;
  });
</script>

<img src={src()} alt="" class="object-cover rounded-[inherit] {className}" draggable="false" />
