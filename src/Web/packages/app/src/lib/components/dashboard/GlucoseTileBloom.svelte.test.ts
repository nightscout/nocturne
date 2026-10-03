import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import type { MountOptions, PlayerState, WasmModule } from "@nocturne/watercolour";
import GlucoseTileBloom from "./GlucoseTileBloom.svelte";

const { mount_player, bloom_scene, dispose } = vi.hoisted(() => ({
  mount_player: vi.fn(), bloom_scene: vi.fn(() => "{}"), dispose: vi.fn(),
}));

vi.mock("@nocturne/watercolour", () => ({ mountPlayer: mount_player, bloomScene: bloom_scene }));

beforeEach(() => {
  vi.clearAllMocks();
  mount_player.mockReturnValue(dispose);
});

describe("GlucoseTileBloom", () => {
  it("reports coverage during playback without rebuilding for host callback changes", async () => {
    const onprogress = vi.fn();
    const onstatechange = vi.fn();
    const view = render(GlucoseTileBloom, { seed: 7, delta: 15, token: "--muted", onprogress, onstatechange });
    await expect.poll(() => mount_player.mock.calls.length).toBe(1);
    const options = mount_player.mock.calls[0]![2] as MountOptions;
    options.scene!({} as WasmModule, 400, 150, 2);
    expect(bloom_scene).toHaveBeenCalledWith(expect.anything(), 400, 150, expect.objectContaining({ seed: 7, slope: 1, dpr: 2 }));
    options.onProgress!(0.68, false);
    expect(onprogress).toHaveBeenCalledWith(0.68);
    const next_progress = vi.fn();
    await view.rerender({ onprogress: next_progress });
    options.onProgress!(0.7, false);
    expect(next_progress).toHaveBeenCalledWith(0.7);
    expect(mount_player).toHaveBeenCalledTimes(1);
    const state = { mode: "none", progress: 0, finished: false } as PlayerState;
    options.onStateChange!(state);
    expect(onstatechange).toHaveBeenCalledWith(state);
  });

  it("disposes the old reveal and captures the next reading's seed and slope", async () => {
    const view = render(GlucoseTileBloom, { seed: 7, delta: 15, token: "--muted", onprogress: vi.fn(), onstatechange: vi.fn() });
    await expect.poll(() => mount_player.mock.calls.length).toBe(1);
    await view.rerender({ seed: 8, delta: -15 });
    expect(dispose).toHaveBeenCalledTimes(1);
    expect(mount_player).toHaveBeenCalledTimes(2);
    const options = mount_player.mock.calls[1]![2] as MountOptions;
    options.scene!({} as WasmModule, 200, 100, 1);
    expect(bloom_scene).toHaveBeenLastCalledWith(expect.anything(), 200, 100, expect.objectContaining({ seed: 8, slope: -1 }));
  });
});
