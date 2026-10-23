import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import { getCustomSounds } from "$lib/audio/alarm-sounds";
import CustomSoundUpload from "./CustomSoundUpload.svelte";

/** The playability probe's own load timeout in alarm-sounds.ts. */
const PROBE_TIMEOUT_MS = 10_000;

/** A tenth of a second of 8 kHz mono 16-bit silence. */
function silentWav(): Uint8Array<ArrayBuffer> {
  const samples = 800;
  const bytes = new Uint8Array(44 + samples * 2);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, text: string) =>
    [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, "RIFF");
  view.setUint32(4, 36 + samples * 2, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, 8000, true);
  view.setUint32(28, 16000, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, "data");
  view.setUint32(40, samples * 2, true);
  return bytes;
}

async function upload(file: File) {
  render(CustomSoundUpload);
  await page.getByRole("button", { name: "Upload Sound" }).click();
  await page.getByLabelText("Audio File").upload(file);
  await page.getByRole("button", { name: "Upload", exact: true }).click();
}

describe("CustomSoundUpload", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("names the accepted formats and the size limit", async () => {
    render(CustomSoundUpload);
    await expect
      .element(page.getByText("Upload your own alarm sounds (MP3, WAV, OGG, FLAC, WebM, M4A, AAC, up to 5 MB)"))
      .toBeVisible();
  });

  it("stores a playable WAV", async () => {
    const fileName = `alarm-${crypto.randomUUID()}.wav`;
    await upload(new File([silentWav()], fileName, { type: "audio/wav" }));

    await expect.element(page.getByText(`${fileName} •`), { timeout: PROBE_TIMEOUT_MS + 5_000 }).toBeVisible();

    const stored = (await getCustomSounds()).find((s) => s.fileName === fileName);
    expect(stored).toBeDefined();
    expect(stored!.mimeType).toBe("audio/wav");
    expect(stored!.dataUrl).toMatch(/^data:audio\/wav;base64,/);
  }, PROBE_TIMEOUT_MS + 10_000);

  it("rejects a file that is not a supported format", async () => {
    await upload(new File(["notes"], "notes.txt", { type: "text/plain" }));
    await expect
      .element(page.getByText("Choose one of these formats: MP3, WAV, OGG, FLAC, WebM, M4A, AAC"))
      .toBeVisible();
  });

  it("rejects a file over the size limit", async () => {
    await upload(new File([new Uint8Array(5 * 1024 * 1024 + 1)], "long.mp3", { type: "audio/mpeg" }));
    await expect.element(page.getByText("The file is larger than 5 MB")).toBeVisible();
  });

  it("rejects a file the browser cannot decode", async () => {
    await upload(new File(["not audio"], "broken.flac", { type: "" }));
    await expect
      .element(page.getByText("This file could not be played. Check that it is a valid audio file."))
      .toBeVisible();
  });

  it("gives up on a file that never finishes loading", async () => {
    let loading = false;
    vi.stubGlobal(
      "Audio",
      class {
        set src(_: string) {}
        load() {
          loading = true;
        }
      }
    );
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });

    await upload(new File([silentWav()], "stalled.wav", { type: "audio/wav" }));
    await vi.waitFor(() => expect(loading).toBe(true));
    await vi.advanceTimersByTimeAsync(PROBE_TIMEOUT_MS);

    await expect.element(page.getByText("The audio file took too long to load")).toBeVisible();
  });
});
