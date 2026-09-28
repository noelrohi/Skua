//! 'U' user input from the Mac App's Game View (ADR 0006): the payload after the id, parsed into the
//! Ruffle `PlayerEvent` it stands for.
//!
//!   u8 kind | fields
//!   1 mouse move    f32 x | f32 y                      (Game Host viewport pixels)
//!   2 mouse down    f32 x | f32 y | u8 button          (0 unknown, 1 left, 2 right, 3 middle)
//!   3 mouse up      f32 x | f32 y | u8 button
//!   4 mouse leave
//!   5 wheel         u8 unit (0 lines, 1 pixels) | f32 delta
//!   6 key down      u8 location | u32 char (0 = none) | name physical | name named   (see `key`)
//!   7 key up        the same
//!   8 text          u32 code point
//!   9 text control  name (a `TextControlCode`, e.g. Backspace)
//!   10 focus gained
//!   11 focus lost
//!
//! A name is a u8 length, then ASCII. Keys go by Ruffle's variant names, not its discriminants, so a
//! Ruffle update that reorders an enum can't change what a key means.

use ruffle_core::events::{
    KeyDescriptor, KeyLocation, LogicalKey, MouseButton, MouseWheelDelta, NamedKey, PhysicalKey, PlayerEvent,
    TextControlCode,
};

/// One input event, as the Engine sent it.
#[derive(Debug, PartialEq)]
pub enum Input {
    MouseMove { x: f64, y: f64 },
    MouseDown { x: f64, y: f64, button: MouseButton },
    MouseUp { x: f64, y: f64, button: MouseButton },
    MouseLeave,
    Wheel(MouseWheelDelta),
    KeyDown(KeyDescriptor),
    KeyUp(KeyDescriptor),
    Text(char),
    TextControl(TextControlCode),
    FocusGained,
    FocusLost,
}

impl Input {
    pub fn into_event(self) -> PlayerEvent {
        match self {
            Input::MouseMove { x, y } => PlayerEvent::MouseMove { x, y },
            Input::MouseDown { x, y, button } => PlayerEvent::MouseDown {
                x,
                y,
                button,
                index: None,
            },
            Input::MouseUp { x, y, button } => PlayerEvent::MouseUp { x, y, button },
            Input::MouseLeave => PlayerEvent::MouseLeave,
            Input::Wheel(delta) => PlayerEvent::MouseWheel { delta },
            Input::KeyDown(key) => PlayerEvent::KeyDown { key },
            Input::KeyUp(key) => PlayerEvent::KeyUp { key },
            Input::Text(codepoint) => PlayerEvent::TextInput { codepoint },
            Input::TextControl(code) => PlayerEvent::TextControl { code },
            Input::FocusGained => PlayerEvent::FocusGained,
            Input::FocusLost => PlayerEvent::FocusLost,
        }
    }
}

struct Reader<'a>(&'a [u8]);

impl Reader<'_> {
    fn u8(&mut self) -> Result<u8, String> {
        let (&b, rest) = self.0.split_first().ok_or("'U' frame too short")?;
        self.0 = rest;
        Ok(b)
    }
    fn bytes(&mut self, n: usize) -> Result<&[u8], String> {
        if self.0.len() < n {
            return Err("'U' frame too short".into());
        }
        let (head, rest) = self.0.split_at(n);
        self.0 = rest;
        Ok(head)
    }
    fn u32(&mut self) -> Result<u32, String> {
        Ok(u32::from_le_bytes(self.bytes(4)?.try_into().unwrap()))
    }
    fn f32(&mut self) -> Result<f64, String> {
        Ok(f32::from_le_bytes(self.bytes(4)?.try_into().unwrap()) as f64)
    }
    fn name(&mut self) -> Result<&str, String> {
        let n = self.u8()? as usize;
        std::str::from_utf8(self.bytes(n)?).map_err(|_| "'U' name isn't ASCII".into())
    }
    fn button(&mut self) -> Result<MouseButton, String> {
        Ok(match self.u8()? {
            1 => MouseButton::Left,
            2 => MouseButton::Right,
            3 => MouseButton::Middle,
            _ => MouseButton::Unknown,
        })
    }
    fn key(&mut self) -> Result<KeyDescriptor, String> {
        let key_location = match self.u8()? {
            1 => KeyLocation::Left,
            2 => KeyLocation::Right,
            3 => KeyLocation::Numpad,
            _ => KeyLocation::Standard,
        };
        let ch = self.u32()?;
        let physical_key = physical_key(self.name()?);
        let named = self.name()?;
        let logical_key = match char::from_u32(ch).filter(|&c| c != '\0') {
            Some(c) => LogicalKey::Character(c),
            None => named_key(named).map_or(LogicalKey::Unknown, LogicalKey::Named),
        };
        Ok(KeyDescriptor {
            physical_key,
            logical_key,
            key_location,
        })
    }
}

/// Parses the 'U' payload after the id.
pub fn parse(payload: &[u8]) -> Result<Input, String> {
    let mut r = Reader(payload);
    Ok(match r.u8()? {
        1 => Input::MouseMove {
            x: r.f32()?,
            y: r.f32()?,
        },
        2 => Input::MouseDown {
            x: r.f32()?,
            y: r.f32()?,
            button: r.button()?,
        },
        3 => Input::MouseUp {
            x: r.f32()?,
            y: r.f32()?,
            button: r.button()?,
        },
        4 => Input::MouseLeave,
        5 => {
            let pixels = r.u8()? == 1;
            let delta = r.f32()?;
            Input::Wheel(if pixels {
                MouseWheelDelta::Pixels(delta)
            } else {
                MouseWheelDelta::Lines(delta)
            })
        }
        6 => Input::KeyDown(r.key()?),
        7 => Input::KeyUp(r.key()?),
        8 => {
            let c = r.u32()?;
            Input::Text(char::from_u32(c).ok_or_else(|| format!("'U' text: not a code point: {c:#x}"))?)
        }
        9 => {
            let name = r.name()?;
            Input::TextControl(text_control(name).ok_or_else(|| format!("'U' text control: unknown code {name}"))?)
        }
        10 => Input::FocusGained,
        11 => Input::FocusLost,
        kind => return Err(format!("'U' frame: unknown kind {kind}")),
    })
}

/// Maps each name to the variant of the same name, and anything else to `None`.
macro_rules! by_name {
    ($fn:ident, $ty:ident, [$($v:ident),* $(,)?]) => {
        fn $fn(name: &str) -> Option<$ty> {
            match name {
                $(stringify!($v) => Some($ty::$v),)*
                _ => None,
            }
        }
    };
}

fn physical_key(name: &str) -> PhysicalKey {
    physical_key_by_name(name).unwrap_or(PhysicalKey::Unknown)
}

by_name!(
    physical_key_by_name,
    PhysicalKey,
    [
        Backquote,
        Digit0,
        Digit1,
        Digit2,
        Digit3,
        Digit4,
        Digit5,
        Digit6,
        Digit7,
        Digit8,
        Digit9,
        Minus,
        Equal,
        IntlYen,
        KeyA,
        KeyB,
        KeyC,
        KeyD,
        KeyE,
        KeyF,
        KeyG,
        KeyH,
        KeyI,
        KeyJ,
        KeyK,
        KeyL,
        KeyM,
        KeyN,
        KeyO,
        KeyP,
        KeyQ,
        KeyR,
        KeyS,
        KeyT,
        KeyU,
        KeyV,
        KeyW,
        KeyX,
        KeyY,
        KeyZ,
        BracketLeft,
        BracketRight,
        Backslash,
        Semicolon,
        Quote,
        IntlBackslash,
        Comma,
        Period,
        Slash,
        IntlRo,
        Backspace,
        Tab,
        CapsLock,
        Enter,
        ShiftLeft,
        ShiftRight,
        ControlLeft,
        SuperLeft,
        AltLeft,
        Space,
        AltRight,
        SuperRight,
        ContextMenu,
        ControlRight,
        Insert,
        Delete,
        Home,
        End,
        PageUp,
        PageDown,
        ArrowUp,
        ArrowLeft,
        ArrowDown,
        ArrowRight,
        NumLock,
        NumpadDivide,
        NumpadMultiply,
        NumpadSubtract,
        Numpad7,
        Numpad8,
        Numpad9,
        Numpad4,
        Numpad5,
        Numpad6,
        Numpad1,
        Numpad2,
        Numpad3,
        Numpad0,
        NumpadAdd,
        NumpadComma,
        NumpadEnter,
        NumpadDecimal,
        Escape,
        F1,
        F2,
        F3,
        F4,
        F5,
        F6,
        F7,
        F8,
        F9,
        F10,
        F11,
        F12,
        F13,
        F14,
        F15,
        F16,
        F17,
        F18,
        F19,
        F20,
        F21,
        F22,
        F23,
        F24,
        F25,
        F26,
        F27,
        F28,
        F29,
        F30,
        F31,
        F32,
        F33,
        F34,
        F35,
        Fn,
        FnLock,
        PrintScreen,
        ScrollLock,
        Pause,
    ]
);

by_name!(
    named_key,
    NamedKey,
    [
        Alt,
        AltGraph,
        CapsLock,
        Control,
        Fn,
        FnLock,
        Super,
        NumLock,
        ScrollLock,
        Shift,
        Symbol,
        SymbolLock,
        Enter,
        Tab,
        ArrowDown,
        ArrowLeft,
        ArrowRight,
        ArrowUp,
        End,
        Home,
        PageDown,
        PageUp,
        Backspace,
        Clear,
        Copy,
        CrSel,
        Cut,
        Delete,
        EraseEof,
        ExSel,
        Insert,
        Paste,
        Redo,
        Undo,
        ContextMenu,
        Escape,
        Pause,
        Play,
        Select,
        ZoomIn,
        ZoomOut,
        PrintScreen,
        F1,
        F2,
        F3,
        F4,
        F5,
        F6,
        F7,
        F8,
        F9,
        F10,
        F11,
        F12,
        F13,
        F14,
        F15,
        F16,
        F17,
        F18,
        F19,
        F20,
        F21,
        F22,
        F23,
        F24,
        F25,
        F26,
        F27,
        F28,
        F29,
        F30,
        F31,
        F32,
        F33,
        F34,
        F35,
    ]
);

by_name!(
    text_control,
    TextControlCode,
    [
        MoveLeft,
        MoveLeftWord,
        MoveLeftLine,
        MoveLeftDocument,
        MoveRight,
        MoveRightWord,
        MoveRightLine,
        MoveRightDocument,
        SelectLeft,
        SelectLeftWord,
        SelectLeftLine,
        SelectLeftDocument,
        SelectRight,
        SelectRightWord,
        SelectRightLine,
        SelectRightDocument,
        SelectAll,
        Copy,
        Paste,
        Cut,
        Backspace,
        BackspaceWord,
        Enter,
        Delete,
        DeleteWord,
    ]
);

#[cfg(test)]
mod tests {
    use super::*;

    fn f32s(values: &[f32]) -> Vec<u8> {
        values.iter().flat_map(|v| v.to_le_bytes()).collect()
    }

    fn name(s: &str) -> Vec<u8> {
        [&[s.len() as u8][..], s.as_bytes()].concat()
    }

    fn key(kind: u8, location: u8, ch: u32, physical: &str, named: &str) -> Vec<u8> {
        [&[kind, location][..], &ch.to_le_bytes(), &name(physical), &name(named)].concat()
    }

    #[test]
    fn parses_mouse_events_in_viewport_pixels() {
        assert_eq!(
            parse(&[&[1][..], &f32s(&[12.5, 300.0])].concat()),
            Ok(Input::MouseMove { x: 12.5, y: 300.0 })
        );
        assert_eq!(
            parse(&[&[2][..], &f32s(&[479.0, 275.0]), &[1]].concat()),
            Ok(Input::MouseDown {
                x: 479.0,
                y: 275.0,
                button: MouseButton::Left
            })
        );
        assert_eq!(
            parse(&[&[3][..], &f32s(&[1.0, 2.0]), &[2]].concat()),
            Ok(Input::MouseUp {
                x: 1.0,
                y: 2.0,
                button: MouseButton::Right
            })
        );
        assert_eq!(parse(&[4]), Ok(Input::MouseLeave));
    }

    #[test]
    fn parses_the_wheel_in_lines_or_pixels() {
        assert_eq!(
            parse(&[&[5, 0][..], &f32s(&[-3.0])].concat()),
            Ok(Input::Wheel(MouseWheelDelta::Lines(-3.0)))
        );
        assert_eq!(
            parse(&[&[5, 1][..], &f32s(&[40.0])].concat()),
            Ok(Input::Wheel(MouseWheelDelta::Pixels(40.0)))
        );
    }

    #[test]
    fn parses_keys_by_name() {
        assert_eq!(
            parse(&key(6, 0, 'a' as u32, "KeyA", "")),
            Ok(Input::KeyDown(KeyDescriptor {
                physical_key: PhysicalKey::KeyA,
                logical_key: LogicalKey::Character('a'),
                key_location: KeyLocation::Standard,
            }))
        );
        assert_eq!(
            parse(&key(7, 1, 0, "ShiftLeft", "Shift")),
            Ok(Input::KeyUp(KeyDescriptor {
                physical_key: PhysicalKey::ShiftLeft,
                logical_key: LogicalKey::Named(NamedKey::Shift),
                key_location: KeyLocation::Left,
            }))
        );
        assert_eq!(
            parse(&key(6, 0, 0, "Tab", "Tab")),
            Ok(Input::KeyDown(KeyDescriptor {
                physical_key: PhysicalKey::Tab,
                logical_key: LogicalKey::Named(NamedKey::Tab),
                key_location: KeyLocation::Standard,
            }))
        );
    }

    #[test]
    fn unknown_key_names_are_unknown_keys_not_errors() {
        assert_eq!(
            parse(&key(6, 0, 0, "Lang1", "Hiragana")),
            Ok(Input::KeyDown(KeyDescriptor {
                physical_key: PhysicalKey::Unknown,
                logical_key: LogicalKey::Unknown,
                key_location: KeyLocation::Standard,
            }))
        );
    }

    #[test]
    fn parses_text_text_control_and_focus() {
        assert_eq!(
            parse(&[&[8][..], &('é' as u32).to_le_bytes()].concat()),
            Ok(Input::Text('é'))
        );
        assert_eq!(
            parse(&[&[9][..], &name("Backspace")].concat()),
            Ok(Input::TextControl(TextControlCode::Backspace))
        );
        assert_eq!(parse(&[10]), Ok(Input::FocusGained));
        assert_eq!(parse(&[11]), Ok(Input::FocusLost));
    }

    #[test]
    fn short_or_unknown_input_is_an_error_not_a_panic() {
        assert!(parse(&[]).is_err());
        assert!(parse(&[1, 0, 0]).is_err());
        assert!(parse(&[2]).is_err());
        assert!(parse(&[6, 0, 97, 0, 0, 0, 9, b'K']).is_err());
        assert!(parse(&[8, 0, 0xD8, 0, 0]).is_err());
        assert!(parse(&[&[9][..], &name("Explode")].concat()).is_err());
        assert!(parse(&[99]).is_err());
    }
}
