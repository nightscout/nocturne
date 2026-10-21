import { describe, expect, it } from "vitest";
import {
  assertAcceptableCustomSound,
  CustomSoundRejectedError,
  MAX_CUSTOM_SOUND_SIZE_MB,
  type CustomSoundRejection,
} from "./alarm-sounds";

const fileOf = (type: string, size = 1024) => new File([new Uint8Array(size)], "sound", { type });

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

  it("rejects a type the picker does not offer", () => {
    expect(rejectionOf(fileOf("video/mp4"))).toBe("unsupported-type");
  });

  it("accepts a file exactly at the size limit and rejects one byte over", () => {
    const limit = MAX_CUSTOM_SOUND_SIZE_MB * 1024 * 1024;
    expect(rejectionOf(fileOf("audio/flac", limit))).toBeUndefined();
    expect(rejectionOf(fileOf("audio/flac", limit + 1))).toBe("too-large");
  });
});
