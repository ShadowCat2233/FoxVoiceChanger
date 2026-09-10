pub mod components;
pub mod foundation_models;
pub mod guard;
pub mod hardware;
pub mod recommendation;
pub mod training;

pub use components::{ComponentManifest, ManifestError};
pub use foundation_models::{
    foundation_root, install as install_foundation_models, status as foundation_model_status,
};
pub use guard::GameGuard;
pub use hardware::detect_hardware;
pub use recommendation::recommend_engine;
pub use training::{install as install_training, status as training_status, training_root};
