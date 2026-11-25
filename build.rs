fn main() {
    // Only run winresource if windres.exe or rc.exe is available on PATH;
    // window icon and DPI awareness are also configured programmatically at runtime.
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default() == "windows" {
        let has_rc = std::process::Command::new("windres")
            .arg("--version")
            .output()
            .is_ok()
            || std::process::Command::new("rc")
                .arg("/?")
                .output()
                .is_ok();
        if has_rc {
            let mut res = winresource::WindowsResource::new();
            res.set_icon("src/ChestCounter.Gui/Assets/token.ico");
            res.set_manifest_file("src/app.manifest");
            let _ = res.compile();
        }
    }
}
