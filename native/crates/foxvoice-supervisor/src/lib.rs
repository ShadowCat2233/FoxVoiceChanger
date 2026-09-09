pub mod components;
pub mod guard;
pub mod hardware;
pub mod recommendation;

pub use components::{ComponentManifest, ManifestError};
pub use guard::GameGuard;
pub use hardware::detect_hardware;
pub use recommendation::recommend_engine;
