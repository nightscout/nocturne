<script lang="ts" module>
  export type IsoBox = { day: Date; height: number; colour: string };
</script>

<script lang="ts">
  import { getChartContext } from "layerchart";
  import { timeMonths, timeWeek } from "d3-time";

  // Spike: a year calendar's boxes drawn by one canvas mark in one pass, in place of one `Rect`
  // component per day. Draws in plot space; the canvas layer applies the isometric matrix.

  let {
    boxes,
    start,
    end,
    gap = 1,
    labelColour = "currentColor",
  }: { boxes: IsoBox[]; start: Date; end: Date; gap?: number; labelColour?: string } = $props();

  const ctx = getChartContext();

  const firstWeek = $derived(timeWeek.floor(start));
  const weekColumns = $derived(timeWeek.count(firstWeek, end) + 1);
  const cellSize = $derived(Math.min(ctx.width / weekColumns, ctx.height / 7));

  /** Footprints in plot space, fixed until the data or the floor changes. */
  const footprints = $derived(
    boxes.map((b) => ({
      x: timeWeek.count(firstWeek, b.day) * cellSize + gap / 2,
      y: b.day.getDay() * cellSize + gap / 2,
      size: cellSize - gap,
      z: b.height,
      colour: b.colour,
    }))
  );

  /** Month labels along the floor's back edge, where `Calendar` puts them. */
  const months = $derived(
    timeMonths(start, end).map((date) => ({
      x: timeWeek.count(firstWeek, timeWeek.ceil(date)) * cellSize,
      label: date.toLocaleDateString(undefined, { month: "short" }),
    }))
  );

  const SIDES = [
    { n: { x: 0, y: -1 }, corners: [0, 1] },
    { n: { x: 1, y: 0 }, corners: [1, 2] },
    { n: { x: 0, y: 1 }, corners: [2, 3] },
    { n: { x: -1, y: 0 }, corners: [3, 0] },
  ] as const;

  function render(c: CanvasRenderingContext2D) {
    const m = ctx.isometricMatrix;
    const lift = ctx.isometricLift ?? { x: 0, y: 0 };
    // Depth grows toward the viewer: m.b * x + m.d * y, as `backToFront` orders marks.
    const depthX = m?.b ?? 0;
    const depthY = m?.d ?? 1;

    // Sides whose outward normal points toward the viewer, with a shade by how far each faces right.
    const visible = SIDES.filter((s) => s.n.x * depthX + s.n.y * depthY > 1e-9).map((s) => {
      const sx = m ? m.a * s.n.x + m.c * s.n.y : s.n.x;
      const sy = m ? m.b * s.n.x + m.d * s.n.y : s.n.y;
      const across = sx / (Math.hypot(sx, sy) || 1);
      return { ...s, shade: 0.12 + 0.2 * ((across + 1) / 2) };
    });

    const order = footprints
      .map((f) => ({ f, depth: depthX * (f.x + f.size / 2) + depthY * (f.y + f.size / 2) }))
      .sort((a, b) => a.depth - b.depth);

    const px = [0, 0, 0, 0];
    const py = [0, 0, 0, 0];
    for (const { f } of order) {
      px[0] = f.x; py[0] = f.y;
      px[1] = f.x + f.size; py[1] = f.y;
      px[2] = f.x + f.size; py[2] = f.y + f.size;
      px[3] = f.x; py[3] = f.y + f.size;
      const dx = lift.x * f.z;
      const dy = lift.y * f.z;

      c.fillStyle = f.colour;
      if (f.z > 0) {
        for (const side of visible) {
          const [i, j] = side.corners;
          c.beginPath();
          c.moveTo(px[i], py[i]);
          c.lineTo(px[j], py[j]);
          c.lineTo(px[j] + dx, py[j] + dy);
          c.lineTo(px[i] + dx, py[i] + dy);
          c.closePath();
          c.fillStyle = f.colour;
          c.fill();
          c.fillStyle = `rgba(0,0,0,${side.shade})`;
          c.fill();
        }
        c.fillStyle = f.colour;
      }
      c.beginPath();
      c.moveTo(px[0] + dx, py[0] + dy);
      c.lineTo(px[1] + dx, py[1] + dy);
      c.lineTo(px[2] + dx, py[2] + dy);
      c.lineTo(px[3] + dx, py[3] + dy);
      c.closePath();
      c.fill();
    }

    c.fillStyle = labelColour;
    c.font = "12px sans-serif";
    c.textBaseline = "bottom";
    for (const month of months) c.fillText(month.label, month.x, -4);
  }

  ctx.registerComponent({
    name: "IsoBoxes",
    kind: "mark",
    canvasRender: {
      render,
      deps: () => [ctx.isometricMatrix, ctx.isometricLift, footprints, months, labelColour],
    },
  });
</script>

