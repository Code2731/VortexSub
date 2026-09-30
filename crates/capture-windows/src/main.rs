#[cfg(windows)]
mod probe;

#[cfg(windows)]
fn main() {
    if let Err(error) = probe::run() {
        eprintln!("capture probe: {error}");
        std::process::exit(1);
    }
}

#[cfg(not(windows))]
fn main() {
    eprintln!("WASAPI loopback probe requires Windows");
    std::process::exit(1);
}
