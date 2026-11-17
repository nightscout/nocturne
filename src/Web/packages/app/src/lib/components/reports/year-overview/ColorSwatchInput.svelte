<script lang="ts">
  import { cssColorToHex } from "#lib/utils/css-color.js";

  let {
    color,
    label,
    onChange,
    class: className = "",
  }: {
    /** The color drawn now, any CSS color. */
    color: string;
    label: string;
    onChange: (hex: string) => void;
    class?: string;
  } = $props();

  // A native input shows only its own hex value, so the swatch draws `color` and the input
  // lies over it, reading the drawn color as it opens.
  function prime(event: Event & { currentTarget: HTMLInputElement }) {
    event.currentTarget.value = cssColorToHex(color, event.currentTarget);
  }
</script>

<span
  class={[
    "relative inline-block size-5 shrink-0 rounded-full border-2 border-foreground/70 bg-(--swatch) shadow-sm focus-within:ring-4 focus-within:ring-ring/50",
    className,
  ]}
  style:--swatch={color}
>
  <!-- eslint-disable-next-line no-restricted-syntax -- native colour picker -->
  <input
    type="color"
    aria-label={label}
    title={label}
    class="absolute inset-0 size-full cursor-pointer opacity-0"
    onpointerdown={prime}
    onfocus={prime}
    oninput={(event) => onChange(event.currentTarget.value)}
  />
</span>
