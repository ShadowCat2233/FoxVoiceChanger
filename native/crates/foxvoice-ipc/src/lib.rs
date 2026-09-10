use std::io::{Read, Write};

use serde::{Serialize, de::DeserializeOwned};
use thiserror::Error;

pub const MAX_FRAME_BYTES: usize = 1024 * 1024;

#[derive(Debug, Error)]
pub enum FrameError {
    #[error("IPC I/O 错误: {0}")]
    Io(#[from] std::io::Error),
    #[error("IPC JSON 错误: {0}")]
    Json(#[from] serde_json::Error),
    #[error("IPC 帧过大: {actual} bytes，最大允许 {maximum} bytes")]
    TooLarge { actual: usize, maximum: usize },
}

pub fn write_frame<W: Write, T: Serialize>(writer: &mut W, value: &T) -> Result<(), FrameError> {
    let payload = serde_json::to_vec(value)?;
    if payload.len() > MAX_FRAME_BYTES {
        return Err(FrameError::TooLarge {
            actual: payload.len(),
            maximum: MAX_FRAME_BYTES,
        });
    }
    writer.write_all(&(payload.len() as u32).to_le_bytes())?;
    writer.write_all(&payload)?;
    writer.flush()?;
    Ok(())
}

pub fn read_frame<R: Read, T: DeserializeOwned>(reader: &mut R) -> Result<T, FrameError> {
    let mut length_bytes = [0_u8; 4];
    reader.read_exact(&mut length_bytes)?;
    let length = u32::from_le_bytes(length_bytes) as usize;
    if length > MAX_FRAME_BYTES {
        return Err(FrameError::TooLarge {
            actual: length,
            maximum: MAX_FRAME_BYTES,
        });
    }
    let mut payload = vec![0_u8; length];
    reader.read_exact(&mut payload)?;
    Ok(serde_json::from_slice(&payload)?)
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;

    use serde::{Deserialize, Serialize};

    use super::*;

    #[derive(Debug, PartialEq, Serialize, Deserialize)]
    struct Message {
        id: u64,
        name: String,
    }

    #[test]
    fn length_prefixed_frame_round_trips() {
        let message = Message {
            id: 7,
            name: "狐声".into(),
        };
        let mut bytes = Vec::new();
        write_frame(&mut bytes, &message).unwrap();
        let decoded: Message = read_frame(&mut Cursor::new(bytes)).unwrap();
        assert_eq!(decoded, message);
    }

    #[test]
    fn rejects_untrusted_oversized_length_before_allocating() {
        let length = (MAX_FRAME_BYTES as u32 + 1).to_le_bytes();
        let error = read_frame::<_, Message>(&mut Cursor::new(length)).unwrap_err();
        assert!(matches!(error, FrameError::TooLarge { .. }));
    }
}
