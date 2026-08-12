//! Stable per-install identifier for client-device registration.
//!
//! The server keys its device upsert on this id (`RegisterDeviceRequest.installId`), so it must be
//! generated once and reused across launches. It is a non-secret correlation id, so it lives as a
//! plain file under `%LOCALAPPDATA%\Nocturne` (next to glucose.json) rather than the credential
//! store, which is reserved for the OAuth tokens in `auth.rs`.

use std::path::PathBuf;

const FILE_NAME: &str = "install_id.txt";

/// Returns `%LOCALAPPDATA%\Nocturne\install_id.txt`.
fn install_id_path() -> PathBuf {
    crate::app_dir::nocturne_dir().join(FILE_NAME)
}

/// Returns the persisted install id, generating and writing a fresh UUID on first launch. A
/// read/write failure falls back to an in-memory UUID for the session so registration can still
/// proceed (the next launch retries the write).
pub fn get_or_create() -> String {
    let path = install_id_path();

    if let Ok(existing) = std::fs::read_to_string(&path) {
        let trimmed = existing.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }

    write_new(&path)
}

/// Replaces the persisted install id with a fresh UUID and returns it. Used when the server reports
/// the current id is registered to a different subject (409 on register) — e.g. the machine was
/// relinked as a different user — which no amount of retrying with the same id can fix.
pub fn regenerate() -> String {
    write_new(&install_id_path())
}

fn write_new(path: &std::path::Path) -> String {
    let id = uuid::Uuid::new_v4().to_string();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    // Write atomically (temp file + same-volume rename) so a crash mid-write can never leave a
    // truncated, corrupt id behind. A failure falls through to the in-memory id for this session.
    let tmp = path.with_extension("txt.tmp");
    if std::fs::write(&tmp, &id).and_then(|()| std::fs::rename(&tmp, path)).is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    id
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_is_nocturne_install_id_txt() {
        let p = install_id_path();
        assert_eq!(p.file_name().unwrap(), "install_id.txt");
        assert_eq!(p.parent().unwrap().file_name().unwrap(), "Nocturne");
    }
}
