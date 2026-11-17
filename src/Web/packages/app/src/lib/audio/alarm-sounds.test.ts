import { describe, expect, it } from "vitest";
import {
  asCanonicalCustomSound,
  resolveCustomSoundType,
  CustomSoundRejectedError,
  MAX_CUSTOM_SOUND_SIZE_MB,
  type CustomSoundRejection,
} from "./alarm-sounds";

const fileOf = (type: string, size = 1024, name = "sound") =>
  new File([new Uint8Array(size)], name, { type });

const rejectionOf = (file: File): CustomSoundRejection | undefined => {
  try {
    resolveCustomSoundType(file);
    return undefined;
  } catch (err) {
    if (err instanceof CustomSoundRejectedError) return err.reason;
    throw err;
  }
};

describe("resolveCustomSoundType", () => {
  it.each(["audio/mpeg", "audio/wav", "audio/ogg", "audio/flac", "audio/webm", "audio/mp4", "audio/aac"])(
    "accepts %s",
    (type) => {
      expect(rejectionOf(fileOf(type))).toBeUndefined();
    }
  );

  it.each([
    ["audio/flac", "alarm.flac", "audio/flac"],
    ["", "alarm.FLAC", "audio/flac"],
    ["audio/x-flac", "a.flac", "audio/flac"],
    ["audio/vnd.dlna.adts", "a.aac", "audio/aac"],
    ["audio/x-m4a", "a.m4a", "audio/mp4"],
    ["video/webm", "a.webm", "audio/webm"],
  ])("resolves %j named %s to %s", (type, name, expected) => {
    expect(resolveCustomSoundType(fileOf(type, 1024, name))).toBe(expected);
  });

  it("rejects an unknown type with an unknown extension", () => {
    expect(rejectionOf(fileOf("video/mp4", 1024, "clip.mp4"))).toBe("unsupported-type");
    expect(rejectionOf(fileOf("", 1024, "notes"))).toBe("unsupported-type");
  });

  it("accepts a file exactly at the size limit and rejects one byte over", () => {
    const limit = MAX_CUSTOM_SOUND_SIZE_MB * 1024 * 1024;
    expect(rejectionOf(fileOf("audio/flac", limit))).toBeUndefined();
    expect(rejectionOf(fileOf("audio/flac", limit + 1))).toBe("too-large");
  });
});

describe("asCanonicalCustomSound", () => {
  it("returns a file already under its canonical type unchanged", () => {
    const file = fileOf("audio/flac", 1024, "alarm.flac");
    expect(asCanonicalCustomSound(file)).toBe(file);
  });

  it.each([
    ["", "alarm.FLAC", "audio/flac"],
    ["audio/vnd.dlna.adts", "a.aac", "audio/aac"],
  ])("re-types %j named %s as %s, keeping its bytes", async (type, name, expected) => {
    const file = new File([new Uint8Array([1, 2, 3])], name, { type });
    const audio = asCanonicalCustomSound(file);
    expect(audio.type).toBe(expected);
    expect(new Uint8Array(await audio.arrayBuffer())).toEqual(new Uint8Array([1, 2, 3]));
  });
});
