//! The user's local opt-in for each device actuation capability.
//!
//! Two things read this: registration, which advertises only the enabled capabilities (the server
//! narrows every intent to rule-requested ∩ advertised, so a capability that isn't advertised is
//! never asked for again), and the reconcile pass, which drops a disabled capability's actuation
//! before the re-registration lands.
//!
//! Persisted as a plain file next to install_id.txt rather than in the webview's localStorage: the
//! actuation loop runs with no window open, so Rust has to read the choice without the frontend. A
//! missing, unreadable, or partial file means enabled — an existing install keeps advertising both.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

use crate::client_devices::{ADVERTISED_CAPABILITIES, NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY};

const FILE_NAME: &str = "capabilities.json";

/// Which capabilities this install may advertise and actuate. Serialized camelCase, matching the
/// frontend's shape; `#[serde(default)]` is what makes an absent field mean enabled.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct DeviceCapabilitySettings {
    pub notify: bool,
    pub tray_flash: bool,
}

impl Default for DeviceCapabilitySettings {
    fn default() -> Self {
        Self { notify: true, tray_flash: true }
    }
}

impl DeviceCapabilitySettings {
    /// Whether `capability` may actuate on this install. An unrecognised capability never can.
    pub fn allows(&self, capability: &str) -> bool {
        match capability {
            NOTIFY_CAPABILITY => self.notify,
            TRAY_FLASH_CAPABILITY => self.tray_flash,
            _ => false,
        }
    }

    /// The capabilities to send on the next (re-)registration.
    pub fn advertised(&self) -> Vec<&'static str> {
        ADVERTISED_CAPABILITIES.into_iter().filter(|c| self.allows(c)).collect()
    }
}

fn settings_path() -> PathBuf {
    crate::app_dir::nocturne_dir().join(FILE_NAME)
}

/// In-memory copy of the persisted settings, so the reconcile pass doesn't read the file every 30s.
/// `None` until first read.
fn cache() -> &'static Mutex<Option<DeviceCapabilitySettings>> {
    static CACHE: OnceLock<Mutex<Option<DeviceCapabilitySettings>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(None))
}

/// The current opt-in set, loading it from disk on first call.
pub fn current() -> DeviceCapabilitySettings {
    let mut cached = match cache().lock() {
        Ok(c) => c,
        Err(_) => return DeviceCapabilitySettings::default(),
    };
    *cached.get_or_insert_with(load)
}

fn load() -> DeviceCapabilitySettings {
    std::fs::read_to_string(settings_path())
        .ok()
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_default()
}

/// Persists `settings` and makes them current. Written atomically (temp file + same-volume rename),
/// like the install id, so a crash mid-write can't leave a truncated file behind. A write failure
/// leaves the previous settings in force so the caller can surface it rather than the app running on
/// a choice that won't survive a restart.
pub fn store(settings: DeviceCapabilitySettings) -> Result<(), String> {
    let path = settings_path();
    let body = serde_json::to_vec(&settings).map_err(|e| e.to_string())?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let tmp = path.with_extension("json.tmp");
    if let Err(e) = std::fs::write(&tmp, &body).and_then(|()| std::fs::rename(&tmp, &path)) {
        let _ = std::fs::remove_file(&tmp);
        return Err(e.to_string());
    }

    if let Ok(mut cached) = cache().lock() {
        *cached = Some(settings);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_is_nocturne_capabilities_json() {
        let p = settings_path();
        assert_eq!(p.file_name().unwrap(), "capabilities.json");
        assert_eq!(p.parent().unwrap().file_name().unwrap(), "Nocturne");
    }

    #[test]
    fn default_advertises_every_capability() {
        let s = DeviceCapabilitySettings::default();
        assert_eq!(s.advertised(), vec![NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY]);
        assert!(s.allows(NOTIFY_CAPABILITY));
        assert!(s.allows(TRAY_FLASH_CAPABILITY));
    }

    #[test]
    fn disabled_capability_is_neither_advertised_nor_allowed() {
        let s = DeviceCapabilitySettings { notify: false, tray_flash: true };
        assert_eq!(s.advertised(), vec![TRAY_FLASH_CAPABILITY]);
        assert!(!s.allows(NOTIFY_CAPABILITY));
    }

    #[test]
    fn both_disabled_advertises_nothing() {
        let s = DeviceCapabilitySettings { notify: false, tray_flash: false };
        assert!(s.advertised().is_empty());
    }

    #[test]
    fn unknown_capability_is_never_allowed() {
        assert!(!DeviceCapabilitySettings::default().allows("torch"));
    }

    #[test]
    fn absent_settings_mean_advertised() {
        // An install that predates the toggles has no file; a partial file leaves the rest enabled.
        let empty: DeviceCapabilitySettings = serde_json::from_str("{}").unwrap();
        assert_eq!(empty, DeviceCapabilitySettings::default());
        let partial: DeviceCapabilitySettings = serde_json::from_str(r#"{"notify":false}"#).unwrap();
        assert!(!partial.notify);
        assert!(partial.tray_flash);
    }

    #[test]
    fn settings_round_trip_as_camel_case() {
        let s = DeviceCapabilitySettings { notify: true, tray_flash: false };
        let json = serde_json::to_string(&s).unwrap();
        assert!(json.contains("trayFlash"));
        assert_eq!(serde_json::from_str::<DeviceCapabilitySettings>(&json).unwrap(), s);
    }
}
