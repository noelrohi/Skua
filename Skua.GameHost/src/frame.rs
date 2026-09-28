//! Bridge frames over stdin/stdout.
//!
//!   frame = u32 LE length (type byte + payload) | u8 type | payload
//!
//! Engine -> Game Host
//!   'C' u32 id | utf8 <invoke> XML            synchronous call into AS3
//!   'S' u32 id | u32 max_width (0 = native)   screenshot
//!   'P' u32 id                                ping (transport-only round trip)
//!   'Q' u32 id                                stats (JSON)
//!   'W' u32 id (0) | u8 live [| u32 width | u32 height | f32 scale]
//!                                             the Game View is live (1: render every 33 ms at the viewport
//!                                             given, and fill the Frame Buffer) or not (0: the headless
//!                                             defaults, at the stage size); no reply
//!   'U' u32 id (0) | u8 kind | fields         user input from the Game View (`input.rs`); no reply
//! Game Host -> Engine
//!   'R' u32 id | utf8 return XML              reply to 'C'
//!   'I' u32 id | u32 w | u32 h | u64 frames | PNG bytes   reply to 'S' (w = h = 0, no PNG: no image);
//!                                             frames is an estimate: time run x frame rate
//!   'P' u32 id                                reply to 'P'
//!   'Q' u32 id | utf8 JSON                    reply to 'Q'
//!   'E' utf8 <invoke> XML                     ExternalInterface.call from AS3 (event)
//!   'F' utf8 text                             AS3 trace() / uncaught AS3 error (flash log)
//!   'L' u8 level (1 error, 2 warn) | utf8 text   Ruffle/wgpu log line (debug log)
//!   'X' utf8 name                             ExternalInterface.addCallback registered
//!   'O' u8 cursor | u8 visible                the Game View's mouse cursor changed (0 arrow, 1 hand, 2 I-beam,
//!                                             3 grab; visible 0 after AS3 Mouse.hide()); only with a Frame Buffer
//!   'K' utf8 text                             the game put text on the clipboard (a Copy or Cut in a text field,
//!                                             or System.setClipboard); only with a Frame Buffer
//!
//! With the `diag` feature the host also answers 'M' (memory stats), 'G' (full GC), 'Y' (census),
//! 'Z' <class> (retainer path), 'B' u32 n (render bench) and 'V' "key=value" (render knobs); see `diag.rs`.

use crate::input::{self, Input};
use std::io::{self, Read};

/// 'L' log levels.
pub const LOG_ERROR: u8 = 1;
pub const LOG_WARN: u8 = 2;

/// Larger frames mean the stream is corrupt; nothing the Engine sends comes close.
pub const MAX_FRAME_LEN: usize = 64 << 20;

pub fn encode(kind: u8, payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(5 + payload.len());
    buf.extend_from_slice(&((payload.len() + 1) as u32).to_le_bytes());
    buf.push(kind);
    buf.extend_from_slice(payload);
    buf
}

pub fn encode_with_id(kind: u8, id: u32, rest: &[u8]) -> Vec<u8> {
    let mut payload = Vec::with_capacity(4 + rest.len());
    payload.extend_from_slice(&id.to_le_bytes());
    payload.extend_from_slice(rest);
    encode(kind, &payload)
}

/// The 'I' reply to a screenshot.
pub fn encode_image(id: u32, width: u32, height: u32, frames: u64, png: &[u8]) -> Vec<u8> {
    let mut rest = Vec::with_capacity(16 + png.len());
    rest.extend_from_slice(&width.to_le_bytes());
    rest.extend_from_slice(&height.to_le_bytes());
    rest.extend_from_slice(&frames.to_le_bytes());
    rest.extend_from_slice(png);
    encode_with_id(b'I', id, &rest)
}

/// The 'L' log line.
pub fn encode_log(level: u8, text: &str) -> Vec<u8> {
    let mut payload = Vec::with_capacity(1 + text.len());
    payload.push(level);
    payload.extend_from_slice(text.as_bytes());
    encode(b'L', &payload)
}

/// The 'O' frame: the Game View's cursor.
pub fn encode_cursor(cursor: u8, visible: bool) -> Vec<u8> {
    encode(b'O', &[cursor, visible as u8])
}

/// Reads one frame as (type, payload). `Ok(None)` means the stream ended, cleanly or mid-frame:
/// either way the Engine is gone. `Err` means the stream is corrupt.
pub fn read_frame(r: &mut impl Read) -> io::Result<Option<(u8, Vec<u8>)>> {
    let mut len = [0u8; 4];
    if !read_all(r, &mut len)? {
        return Ok(None);
    }
    let len = u32::from_le_bytes(len) as usize;
    if len == 0 || len > MAX_FRAME_LEN {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("bad frame length {len}"),
        ));
    }
    let mut body = vec![0u8; len];
    if !read_all(r, &mut body)? {
        return Ok(None);
    }
    let payload = body.split_off(1);
    Ok(Some((body[0], payload)))
}

/// `read_exact` that reports EOF as `false` instead of an error.
fn read_all(r: &mut impl Read, buf: &mut [u8]) -> io::Result<bool> {
    match r.read_exact(buf) {
        Ok(()) => Ok(true),
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => Ok(false),
        Err(e) => Err(e),
    }
}

/// A request from the Engine.
#[derive(Debug, PartialEq)]
pub enum Request {
    Call {
        id: u32,
        xml: String,
    },
    Screenshot {
        id: u32,
        max_width: u32,
    },
    Ping {
        id: u32,
    },
    Stats {
        id: u32,
    },
    View {
        live: bool,
        /// The Game View's size in device pixels while live; `None` keeps the stage size.
        viewport: Option<Viewport>,
    },
    Input(Input),
    #[cfg(feature = "diag")]
    Diag {
        id: u32,
        kind: u8,
        arg: Vec<u8>,
    },
}

/// The size the Game View shows the stage at: device pixels and the display's scale factor.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Viewport {
    pub width: u32,
    pub height: u32,
    pub scale: f64,
}

/// Parses a frame from the Engine. An unknown type or a short payload is an error the host logs and
/// skips: the framing itself is still intact.
pub fn parse_request(kind: u8, payload: &[u8]) -> Result<Request, String> {
    let id = read_u32(payload, 0).ok_or_else(|| format!("'{}' frame without an id", kind as char))?;
    let rest = &payload[4..];
    match kind {
        b'C' => Ok(Request::Call {
            id,
            xml: String::from_utf8_lossy(rest).into_owned(),
        }),
        b'S' => {
            let max_width = read_u32(rest, 0).ok_or("'S' frame without max_width")?;
            Ok(Request::Screenshot { id, max_width })
        }
        b'P' => Ok(Request::Ping { id }),
        b'Q' => Ok(Request::Stats { id }),
        b'W' => Ok(Request::View {
            live: *rest.first().ok_or("'W' frame without live")? != 0,
            viewport: match (read_u32(rest, 1), read_u32(rest, 5), read_u32(rest, 9)) {
                (Some(width), Some(height), Some(scale)) => Some(Viewport {
                    width,
                    height,
                    scale: f32::from_bits(scale) as f64,
                }),
                _ => None,
            },
        }),
        b'U' => input::parse(rest).map(Request::Input),
        #[cfg(feature = "diag")]
        b'M' | b'G' | b'Y' | b'Z' | b'B' | b'V' => Ok(Request::Diag {
            id,
            kind,
            arg: rest.to_vec(),
        }),
        _ => Err(format!("unknown frame type 0x{kind:02x}")),
    }
}

fn read_u32(buf: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_le_bytes(buf.get(at..at + 4)?.try_into().ok()?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn request(kind: u8, id: u32, rest: &[u8]) -> Vec<u8> {
        encode_with_id(kind, id, rest)
    }

    #[test]
    fn encodes_length_type_and_payload() {
        assert_eq!(encode(b'F', b"hi"), vec![3, 0, 0, 0, b'F', b'h', b'i']);
        assert_eq!(encode(b'X', b""), vec![1, 0, 0, 0, b'X']);
    }

    #[test]
    fn encodes_the_id_little_endian_after_the_type() {
        assert_eq!(
            encode_with_id(b'R', 0x0102_0304, b"<true/>"),
            [&[12, 0, 0, 0, b'R', 4, 3, 2, 1][..], b"<true/>"].concat()
        );
        assert_eq!(encode_with_id(b'P', 7, b""), vec![5, 0, 0, 0, b'P', 7, 0, 0, 0]);
    }

    #[test]
    fn encodes_an_image_reply() {
        let frame = encode_image(9, 958, 550, 74, b"PNG");
        assert_eq!(&frame[..4], &(1 + 4 + 16 + 3u32).to_le_bytes());
        assert_eq!(frame[4], b'I');
        assert_eq!(&frame[5..9], &9u32.to_le_bytes());
        assert_eq!(&frame[9..13], &958u32.to_le_bytes());
        assert_eq!(&frame[13..17], &550u32.to_le_bytes());
        assert_eq!(&frame[17..25], &74u64.to_le_bytes());
        assert_eq!(&frame[25..], b"PNG");
    }

    #[test]
    fn encodes_the_cursor() {
        assert_eq!(encode_cursor(1, true), vec![3, 0, 0, 0, b'O', 1, 1]);
        assert_eq!(encode_cursor(0, false), vec![3, 0, 0, 0, b'O', 0, 0]);
    }

    #[test]
    fn encodes_a_log_line_with_its_level() {
        assert_eq!(encode_log(1, "boom"), vec![6, 0, 0, 0, b'L', 1, b'b', b'o', b'o', b'm']);
    }

    #[test]
    fn reads_frames_in_order_until_eof() {
        let mut stream = Cursor::new([request(b'P', 1, b""), request(b'C', 2, b"<invoke/>")].concat());
        assert_eq!(read_frame(&mut stream).unwrap(), Some((b'P', vec![1, 0, 0, 0])));
        assert_eq!(
            read_frame(&mut stream).unwrap(),
            Some((b'C', [&[2, 0, 0, 0][..], b"<invoke/>"].concat()))
        );
        assert_eq!(read_frame(&mut stream).unwrap(), None);
    }

    #[test]
    fn eof_mid_frame_ends_the_stream() {
        let frame = request(b'C', 1, b"<invoke/>");
        for cut in [1, 4, 6, frame.len() - 1] {
            assert_eq!(
                read_frame(&mut Cursor::new(&frame[..cut])).unwrap(),
                None,
                "cut at {cut}"
            );
        }
    }

    #[test]
    fn rejects_zero_and_oversized_lengths() {
        assert!(read_frame(&mut Cursor::new(vec![0, 0, 0, 0])).is_err());
        let too_big = ((MAX_FRAME_LEN + 1) as u32).to_le_bytes();
        assert!(read_frame(&mut Cursor::new(too_big.to_vec())).is_err());
    }

    #[test]
    fn parses_engine_requests() {
        assert_eq!(
            parse_request(b'C', &[&5u32.to_le_bytes()[..], b"<invoke name=\"x\"/>"].concat()),
            Ok(Request::Call {
                id: 5,
                xml: "<invoke name=\"x\"/>".into()
            })
        );
        assert_eq!(
            parse_request(b'S', &[5u32.to_le_bytes(), 480u32.to_le_bytes()].concat()),
            Ok(Request::Screenshot { id: 5, max_width: 480 })
        );
        assert_eq!(parse_request(b'P', &5u32.to_le_bytes()), Ok(Request::Ping { id: 5 }));
        assert_eq!(parse_request(b'Q', &5u32.to_le_bytes()), Ok(Request::Stats { id: 5 }));
        assert_eq!(
            parse_request(b'W', &[0, 0, 0, 0, 1]),
            Ok(Request::View {
                live: true,
                viewport: None
            })
        );
        assert_eq!(
            parse_request(b'W', &[0, 0, 0, 0, 0]),
            Ok(Request::View {
                live: false,
                viewport: None
            })
        );
        let retina = [
            &[0, 0, 0, 0, 1][..],
            &1916u32.to_le_bytes(),
            &1100u32.to_le_bytes(),
            &2f32.to_le_bytes(),
        ]
        .concat();
        assert_eq!(
            parse_request(b'W', &retina),
            Ok(Request::View {
                live: true,
                viewport: Some(Viewport {
                    width: 1916,
                    height: 1100,
                    scale: 2.0
                })
            })
        );
        assert_eq!(
            parse_request(b'U', &[0, 0, 0, 0, 10]),
            Ok(Request::Input(Input::FocusGained))
        );
        let click = [&[0, 0, 0, 0, 2][..], &10f32.to_le_bytes(), &20f32.to_le_bytes(), &[1]].concat();
        assert_eq!(
            parse_request(b'U', &click),
            Ok(Request::Input(Input::MouseDown {
                x: 10.0,
                y: 20.0,
                button: ruffle_core::events::MouseButton::Left
            }))
        );
    }

    #[test]
    fn short_or_unknown_requests_are_errors_not_panics() {
        assert!(parse_request(b'P', &[]).is_err());
        assert!(parse_request(b'C', &[1, 0]).is_err());
        assert!(parse_request(b'S', &5u32.to_le_bytes()).is_err());
        assert!(parse_request(b'S', &[5, 0, 0, 0, 1, 2]).is_err());
        assert!(parse_request(b'!', &5u32.to_le_bytes()).is_err());
        assert!(parse_request(b'W', &0u32.to_le_bytes()).is_err());
        assert!(parse_request(b'U', &0u32.to_le_bytes()).is_err());
        assert!(parse_request(b'U', &[0, 0, 0, 0, 2, 1]).is_err());
    }

    #[cfg(not(feature = "diag"))]
    #[test]
    fn diagnostics_frames_are_unknown_without_the_diag_feature() {
        for kind in *b"MGYZBV" {
            assert!(parse_request(kind, &5u32.to_le_bytes()).is_err());
        }
    }
}
