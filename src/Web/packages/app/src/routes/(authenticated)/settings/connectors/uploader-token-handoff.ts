/**
 * The uploader setup dialog closes to hand an API-key request over to the token dialog, because
 * two open dialogs layer by declaration order and the token dialog is declared first. This
 * remembers that the hand-off happened, so the instructions can be brought back when the token
 * dialog closes — and only then: the API tokens section opens the same dialog on its own.
 */
export function createUploaderTokenHandoff() {
  let requestedByUploader = false;

  return {
    /** Records that the token dialog about to open was asked for by the uploader dialog. */
    handOff() {
      requestedByUploader = true;
    },

    /** True when the token dialog closing should bring the uploader dialog back. */
    resumes() {
      if (!requestedByUploader) return false;
      requestedByUploader = false;
      return true;
    },
  };
}
