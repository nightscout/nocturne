#![allow(dead_code)]

//! Owns the local file the taskbar mod / Win11 widget read and an atomic write to it.
//! The file content is the raw Nocturne `V4SummaryResponse` (see `glucose_poll`) — there is no
//! bespoke DTO here; this module only knows the path and how to write bytes safely.

use std::path::PathBuf;

/// Returns `%LOCALAPPDATA%\Nocturne\glucose.json`, falling back to
/// `%USERPROFILE%\AppData\Local\Nocturne\glucose.json` when `LOCALAPPDATA` is unset.
pub fn glucose_file_path() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA")
        .ok()
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            let mut p = std::env::var("USERPROFILE").map(PathBuf::from).unwrap_or_default();
            p.push("AppData");
            p.push("Local");
            p
        });
    base.join("Nocturne").join("glucose.json")
}

/// Writes `bytes` to the glucose file via a temp-file + atomic rename, so a reader never observes
/// a half-written file. Creates the parent directory if needed.
pub fn write_glucose_file(bytes: &[u8]) -> std::io::Result<()> {
    let target = glucose_file_path();
    if let Some(parent) = target.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let tmp = target.with_extension("json.tmp");
    std::fs::write(&tmp, bytes)?;
    std::fs::rename(&tmp, &target)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_is_nocturne_glucose_json() {
        let p = glucose_file_path();
        assert_eq!(p.file_name().unwrap(), "glucose.json");
        assert_eq!(p.parent().unwrap().file_name().unwrap(), "Nocturne");
    }
}
