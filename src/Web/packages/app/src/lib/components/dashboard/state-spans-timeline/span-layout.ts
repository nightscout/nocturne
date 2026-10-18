/**
 * Pixel layout for one timeline track.
 *
 * A span shorter than {@link MIN_SPAN_WIDTH_PX} on screen is widened to that floor, and the
 * extra pixels are taken from the free space beside it, then from the neighbouring spans that
 * have room to spare above the floor. The track keeps its total length, so a one-minute state
 * becomes a visible bar while the hours around it lose at most a few pixels at the seam and
 * nothing further along the track moves. Only a cluster of floor-width spans with no room on
 * either side pushes its right neighbours, and then only by what the cluster itself needs.
 * Spans are clipped to the visible range first, so an open span stops at the plot edge.
 */
export const MIN_SPAN_WIDTH_PX = 4;

export interface TimedSpan {
  startTime: Date;
  endTime: Date | null;
}

export interface LaidOutSpan<T extends TimedSpan> {
  span: T;
  x: number;
  width: number;
}

interface Segment<T> {
  span: T;
  start: number;
  end: number;
}

export function layoutTrack<T extends TimedSpan>(
  spans: readonly T[],
  xScale: (date: Date) => number,
  range: { from: Date; to: Date },
  minWidth: number = MIN_SPAN_WIDTH_PX,
): LaidOutSpan<T>[] {
  const rangeStart = xScale(range.from);
  const rangeEnd = xScale(range.to);

  const segments: Segment<T>[] = [...spans]
    .filter((span) => (span.endTime ?? range.to) > range.from && span.startTime < range.to)
    .sort((a, b) => a.startTime.getTime() - b.startTime.getTime())
    .map((span) => ({
      span,
      start: Math.max(xScale(span.startTime), rangeStart),
      end: Math.min(xScale(span.endTime ?? range.to), rangeEnd),
    }));

  for (let i = 0; i < segments.length; i++) {
    const seg = segments[i];
    const prev = segments[i - 1];
    const next = segments[i + 1];
    // A push from the previous pass may have left this span under the previous one; the
    // overlap is already the previous span's, so this one starts where that one ends.
    if (prev && seg.start < prev.end) {
      seg.start = prev.end;
      seg.end = Math.max(seg.end, seg.start);
    }
    let deficit = minWidth - (seg.end - seg.start);
    if (deficit <= 0) continue;

    // Free space to the right, up to the next span or the plot edge.
    const rightFree = Math.max((next ? next.start : rangeEnd) - seg.end, 0);
    const growRight = Math.min(deficit, rightFree);
    seg.end += growRight;
    deficit -= growRight;

    // Room the right neighbour can give up without dropping below the floor itself.
    if (deficit > 0 && next) {
      const nextWidth = next.end - Math.max(next.start, seg.end);
      const take = Math.min(deficit, Math.max(nextWidth - minWidth, 0));
      seg.end += take;
      next.start = seg.end;
      deficit -= take;
    }

    // Then the same on the left: free space first, then the previous span's room.
    if (deficit > 0) {
      const leftFree = Math.max(seg.start - (prev ? prev.end : rangeStart), 0);
      const growLeft = Math.min(deficit, leftFree);
      seg.start -= growLeft;
      deficit -= growLeft;
    }
    if (deficit > 0 && prev) {
      const take = Math.min(deficit, Math.max(prev.end - prev.start - minWidth, 0));
      seg.start -= take;
      prev.end -= take;
      deficit -= take;
    }

    // Nothing left to borrow: push the right neighbour, which will borrow in turn on its pass.
    if (deficit > 0) {
      seg.end += deficit;
    }
  }

  return segments.map(({ span, start, end }) => ({ span, x: start, width: end - start }));
}
