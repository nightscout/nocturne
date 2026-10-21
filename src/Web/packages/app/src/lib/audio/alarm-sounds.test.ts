import { describe, expect, it } from "vitest";
import {
  assertAcceptableCustomSound,
  CustomSoundRejectedError,
  MAX_CUSTOM_SOUND_SIZE_MB,
  type CustomSoundRejection,
} from "./alarm-sounds";

const fileOf = (type: string, size = 1024, name = "sound") =>
  new File([new Uint8Array(size)], name, { type });

const rejectionOf = (file: File): CustomSoundRejection | undefined => {
  try {
    assertAcceptableCustomSound(file);
    return undefined;
  } catch (err) {
    if (err instanceof CustomSoundRejectedError) return err.reason;
    throw err;
  }
};

describe("assertAcceptableCustomSound", () => {
  it.each(["audio/mpeg", "audio/wav", "audio/ogg", "audio/flac", "audio/webm", "audio/mp4", "audio/aac"])(
    "accepts %s",
    (type) => {
      expect(rejectionOf(fileOf(type))).toBeUndefined();
    }
  );

  it.each([
    ["audio/x-m4a", "alarm.m4a"],
    ["video/webm", "alarm.webm"],
    ["", "alarm.FLAC"],
  ])("accepts a browser-specific type %j when the extension is known (%s)", (type, name) => {
    expect(rejectionOf(fileOf(type, 1024, name))).toBeUndefined();
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
