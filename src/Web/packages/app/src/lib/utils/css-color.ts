/**
 * The `#rrggbb` that `color` draws as inside `host`, over the page background, for a native
 * color input. Resolves theme variables and `color-mix()`, which the input cannot read.
 */
export function cssColorToHex(color: string, host: Element): string {
  const probe = document.createElement("span");
  probe.style.display = "none";
  probe.style.color = color;
  host.appendChild(probe);
  const resolved = getComputedStyle(probe).color;
  probe.remove();

  const context = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
  if (!context) return "#000000";
  context.fillStyle = getComputedStyle(document.body).backgroundColor;
  context.fillRect(0, 0, 1, 1);
  context.fillStyle = resolved;
  context.fillRect(0, 0, 1, 1);
  const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
  return `#${[r, g, b].map((channel) => channel.toString(16).padStart(2, "0")).join("")}`;
}
