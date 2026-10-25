import { afterEach, describe, expect, it, vi } from 'vitest';
import { MAX_BAKED_FRAMES, MAX_BAKED_FRAME_EDGE, MAX_BAKED_UPSCALE, MAX_SHARED_STILLS, bakedServesEdge, clearSharedStills, drawStill, drawStripFrame, parseBakedManifest, sharedStill, sharedStrip, stripFramePosition } from './baked';
import { WatercolourError } from './errors';

const valid = { version: 1, frames: 12, width: 256, height: 256, durationMs: 600, layout: 'vertical' };

describe('cropped fallback frames', () => {
  it('uses the same source window in both neighbouring strip frames', () => {
    const draw_image = vi.fn();
    const context = { clearRect() {}, drawImage: draw_image } as unknown as CanvasRenderingContext2D;
    const bitmap = { width: 256, height: 512 } as ImageBitmap;
    const manifest = parseBakedManifest({ ...valid, frames: 2 });
    drawStripFrame(context, { bitmap, manifest }, 0.5, 80, 30, { x: 0.25, y: 0.5, width: 0.5, height: 0.25 });
    expect(draw_image.mock.calls).toEqual([
      [bitmap, 64, 128, 128, 64, 0, 0, 80, 30],
      [bitmap, 64, 384, 128, 64, 0, 0, 80, 30],
    ]);
  });

  it('crops the final still at its decoded dimensions', () => {
    const draw_image = vi.fn();
    const context = { clearRect() {}, drawImage: draw_image } as unknown as CanvasRenderingContext2D;
    const bitmap = { width: 800, height: 400 } as ImageBitmap;
    drawStill(context, bitmap, 80, 30, { x: 0.25, y: 0.5, width: 0.5, height: 0.25 });
    expect(draw_image).toHaveBeenCalledWith(bitmap, 200, 200, 400, 100, 0, 0, 80, 30);
  });
});

describe('parseBakedManifest', () => {
  it('accepts the baked format and a JSON string of it', () => {
    expect(parseBakedManifest(valid)).toEqual(valid);
    expect(parseBakedManifest(JSON.stringify(valid))).toEqual(valid);
  });

  it('rejects other versions with a typed error', () => {
    const attempt = () => parseBakedManifest({ ...valid, version: 2 });
    expect(attempt).toThrow(WatercolourError);
    expect(attempt).toThrow(/version 2/);
    try {
      attempt();
    } catch (error) {
      expect((error as WatercolourError).code).toBe('UnsupportedVersion');
    }
  });

  it('enforces the frame and edge caps and the layout', () => {
    expect(() => parseBakedManifest({ ...valid, frames: MAX_BAKED_FRAMES + 1 })).toThrow(/frames/);
    expect(() => parseBakedManifest({ ...valid, frames: 0 })).toThrow(/frames/);
    expect(() => parseBakedManifest({ ...valid, width: MAX_BAKED_FRAME_EDGE + 1 })).toThrow(/edges/);
    expect(() => parseBakedManifest({ ...valid, height: 2.5 })).toThrow(/edges/);
    expect(() => parseBakedManifest({ ...valid, layout: 'horizontal' })).toThrow(/layout/);
    expect(() => parseBakedManifest({ ...valid, durationMs: 0 })).toThrow(/durationMs/);
    expect(() => parseBakedManifest('not json')).toThrow(/not JSON/);
    expect(() => parseBakedManifest(null)).toThrow(/not an object/);
  });
});

describe('stripFramePosition', () => {
  it('lands on exact frames at the ends and blends between neighbours', () => {
    expect(stripFramePosition(0, 12)).toEqual({ from: 0, to: 1, blend: 0 });
    expect(stripFramePosition(1, 12)).toEqual({ from: 11, to: 11, blend: 0 });
    const mid = stripFramePosition(0.5, 12);
    expect(mid.from).toBe(5);
    expect(mid.to).toBe(6);
    expect(mid.blend).toBeCloseTo(0.5);
  });

  it('clamps out-of-range progress and degenerate frame counts', () => {
    expect(stripFramePosition(-1, 12)).toEqual({ from: 0, to: 1, blend: 0 });
    expect(stripFramePosition(2, 12)).toEqual({ from: 11, to: 11, blend: 0 });
    expect(stripFramePosition(Number.NaN, 12)).toEqual({ from: 11, to: 11, blend: 0 });
    expect(stripFramePosition(0.3, 1)).toEqual({ from: 0, to: 0, blend: 0 });
  });

  it('is monotone in progress', () => {
    let last = 0;
    for (let i = 0; i <= 100; i++) {
      const { from, blend } = stripFramePosition(i / 100, 16);
      const position = from + blend;
      expect(position).toBeGreaterThanOrEqual(last);
      last = position;
    }
    expect(last).toBe(15);
  });

  it('maps progress linearly with no hold on the final frame', () => {
    // Strips are baked at wall-clock spacing; progress maps straight onto
    // the strip, so a progress inside the settling tail still crosses frames
    // instead of stalling on the last one.
    expect(stripFramePosition(0.8, 12).from).toBe(8);
    expect(stripFramePosition(0.8, 12).to).toBe(9);
    expect(stripFramePosition(0.8, 12).blend).toBeCloseTo(0.8);
    expect(stripFramePosition(0.95, 12).from).toBe(10);
    expect(stripFramePosition(0.95, 12).to).toBe(11);
    expect(stripFramePosition(0.95, 12).blend).toBeCloseTo(0.45);
    expect(stripFramePosition(0.99, 12).from).toBe(10);
    expect(stripFramePosition(0.99, 12).to).toBe(11);
    expect(stripFramePosition(0.99, 12).blend).toBeCloseTo(0.89);
  });
});

describe('bakedServesEdge (strip vs static at size)', () => {
  it('keeps baked while the frame is not being blown up', () => {
    expect(bakedServesEdge(96)).toBe(true);
    expect(bakedServesEdge(MAX_BAKED_FRAME_EDGE)).toBe(true);
    expect(bakedServesEdge(MAX_BAKED_FRAME_EDGE * MAX_BAKED_UPSCALE)).toBe(true);
  });

  it('withholds baked once a hero-sized canvas would enlarge a thumbnail', () => {
    expect(bakedServesEdge(MAX_BAKED_FRAME_EDGE * MAX_BAKED_UPSCALE + 1)).toBe(false);
    // A 400px hero on a 2x display: the case that read as pixelated on dark.
    expect(bakedServesEdge(800)).toBe(false);
  });
});

describe('sharedStill (one decode, many marks)', () => {
  const bitmap = (id: number) => ({ id, close: vi.fn() }) as unknown as ImageBitmap;

  function stubDecoder() {
    let decodes = 0;
    vi.stubGlobal('fetch', async (url: string) => ({
      ok: true,
      blob: async () => ({ url }) as unknown as Blob,
    }));
    vi.stubGlobal('createImageBitmap', async () => {
      decodes += 1;
      return bitmap(decodes);
    });
    return () => decodes;
  }

  afterEach(() => {
    clearSharedStills();
    vi.unstubAllGlobals();
  });

  it('decodes a still once however many marks draw it', async () => {
    const decodes = stubDecoder();
    const first = await sharedStill('/a.png');
    const second = await sharedStill('/a.png');
    const third = await sharedStill('/a.png');
    expect(decodes()).toBe(1);
    expect(second).toBe(first);
    expect(third).toBe(first);
  });

  it('keeps different stills apart', async () => {
    const decodes = stubDecoder();
    expect(await sharedStill('/a.png')).not.toBe(await sharedStill('/b.png'));
    expect(decodes()).toBe(2);
  });

  it('serves a mark that scrolled away and came back without decoding again', async () => {
    const decodes = stubDecoder();
    await sharedStill('/a.png');
    await sharedStill('/b.png');
    await sharedStill('/a.png');
    expect(decodes()).toBe(2);
  });

  it('never closes what it lends, because a backend redraws on resize', async () => {
    stubDecoder();
    const image = await sharedStill('/a.png');
    for (let i = 0; i < MAX_SHARED_STILLS + 4; i++) await sharedStill(`/fill-${i}.png`);
    expect(image.close).not.toHaveBeenCalled();
  });

  it('holds only the stills a page is likely to be showing', async () => {
    const decodes = stubDecoder();
    for (let i = 0; i < MAX_SHARED_STILLS + 2; i++) await sharedStill(`/s-${i}.png`);
    // The first is long gone, the last is still there.
    await sharedStill('/s-0.png');
    expect(decodes()).toBe(MAX_SHARED_STILLS + 3);
    await sharedStill(`/s-${MAX_SHARED_STILLS + 1}.png`);
    expect(decodes()).toBe(MAX_SHARED_STILLS + 3);
  });

  it('does not cache a failure, so the next mark can try again', async () => {
    let attempts = 0;
    vi.stubGlobal('fetch', async () => {
      attempts += 1;
      return { ok: false, status: 404 };
    });
    await expect(sharedStill('/missing.png')).rejects.toThrow();
    await expect(sharedStill('/missing.png')).rejects.toThrow();
    expect(attempts).toBe(2);
  });
});

describe('sharedStrip (one decode per artwork and palette)', () => {
  const manifest = parseBakedManifest({ version: 1, frames: 10, width: 16, height: 16, durationMs: 600, layout: 'vertical' });

  afterEach(() => {
    clearSharedStills();
    vi.unstubAllGlobals();
  });

  it('decodes a strip once for every baked reveal of it, and never closes it', async () => {
    let decodes = 0;
    const close = vi.fn();
    vi.stubGlobal('fetch', async () => ({ ok: true, blob: async () => ({}) as Blob }));
    vi.stubGlobal('createImageBitmap', async () => {
      decodes += 1;
      return { width: 16, height: 160, close } as unknown as ImageBitmap;
    });

    const first = await sharedStrip('/tab/strip.webp', manifest);
    const second = await sharedStrip('/tab/strip.webp', manifest);

    expect(decodes).toBe(1);
    expect(second).toBe(first);
    expect(close).not.toHaveBeenCalled();
  });
});
