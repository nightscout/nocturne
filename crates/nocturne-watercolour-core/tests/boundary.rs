//! Enforces the layer rule: the domain never reaches into the application
//! layer, and the crate never names a platform dependency.

use std::fs;
use std::path::{Path, PathBuf};

fn rust_files(dir: &Path, out: &mut Vec<PathBuf>) {
    for entry in fs::read_dir(dir).expect("readable source dir") {
        let path = entry.expect("dir entry").path();
        if path.is_dir() {
            rust_files(&path, out);
        } else if path.extension().is_some_and(|e| e == "rs") {
            out.push(path);
        }
    }
}

fn src_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("src")
}

#[test]
fn domain_does_not_use_application() {
    let mut files = Vec::new();
    rust_files(&src_root().join("domain"), &mut files);
    assert!(!files.is_empty());
    for f in files {
        let text = fs::read_to_string(&f).unwrap();
        for (n, line) in text.lines().enumerate() {
            let t = line.trim_start();
            assert!(
                !(t.starts_with("use crate::application") || t.contains("crate::application::")),
                "{}:{} reaches into application: {line}",
                f.display(),
                n + 1
            );
        }
    }
}

#[test]
fn crate_has_no_platform_dependencies() {
    let mut files = Vec::new();
    rust_files(&src_root(), &mut files);
    let banned = ["wgpu", "serde", "web_sys", "js_sys"];
    for f in files {
        let text = fs::read_to_string(&f).unwrap();
        for (n, line) in text.lines().enumerate() {
            let t = line.trim_start();
            if t.starts_with("use ") || t.starts_with("extern crate ") {
                for b in banned {
                    assert!(
                        !t.contains(b),
                        "{}:{} names {b}: {line}",
                        f.display(),
                        n + 1
                    );
                }
            }
        }
    }
    let manifest =
        fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml")).unwrap();
    let deps = manifest.split("[dependencies]").nth(1).unwrap_or("");
    let deps = deps.split('[').next().unwrap_or("");
    assert!(
        deps.lines()
            .all(|l| l.trim().is_empty() || l.trim_start().starts_with('#')),
        "core crate must have no dependencies, found:\n{deps}"
    );
}
