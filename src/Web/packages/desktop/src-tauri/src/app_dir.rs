//! The companion's per-user data directory, shared by every plain file it persists (glucose.json,
//! install_id.txt, capabilities.json). Secrets live in the credential store instead (`auth.rs`).

use std::path::PathBuf;

/// Returns `%LOCALAPPDATA%\Nocturne`, falling back to `%USERPROFILE%\AppData\Local\Nocturne` when
/// `LOCALAPPDATA` is unset.
pub fn nocturne_dir() -> PathBuf {
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
    base.join("Nocturne")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dir_is_named_nocturne() {
        assert_eq!(nocturne_dir().file_name().unwrap(), "Nocturne");
    }
}
