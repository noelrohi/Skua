//! The game's picture in the Game tab, drawn over the cells the UI leaves blank for it with the kitty graphics protocol, which Ghostty,
//! kitty and WezTerm speak. The Engine's screenshot is already a base64 PNG, which the protocol takes as is (`f=100`).

use std::io::{self, Write};

use ratatui::layout::Rect;

/// The one image skua-tui shows, at its one placement: sending it again replaces both, so a new frame doesn't flicker.
const IMAGE_ID: u32 = 7717;
const PLACEMENT_ID: u32 = 1;
/// The protocol's largest chunk of payload per escape.
const CHUNK: usize = 4096;

/// Whether the terminal draws kitty graphics: Ghostty and kitty say so in their environment, WezTerm in `TERM_PROGRAM`.
pub fn supported() -> bool {
    let var = |name: &str| std::env::var(name).unwrap_or_default();
    var("TERM").contains("ghostty")
        || var("TERM").contains("kitty")
        || matches!(var("TERM_PROGRAM").as_str(), "ghostty" | "WezTerm")
        || std::env::var_os("KITTY_WINDOW_ID").is_some()
        || std::env::var_os("GHOSTTY_RESOURCES_DIR").is_some()
}

/// What the terminal shows now, so a picture is sent again only when its frame or place changes.
#[derive(Debug, Default)]
pub struct Shown {
    current: Option<(String, i64, Rect)>,
}

impl Shown {
    /// Shows `png` (base64) of `engine`'s `frame` in `area`, or takes the picture away when there is none to show.
    pub fn sync(&mut self, out: &mut impl Write, want: Option<(&str, i64, &str, Rect)>) -> io::Result<()> {
        match want {
            Some((engine, frame, png, area)) => {
                if self
                    .current
                    .as_ref()
                    .is_some_and(|(e, f, a)| e == engine && *f == frame && *a == area)
                {
                    return Ok(());
                }
                // The cursor goes to the area's top left; the picture is scaled into its cells and leaves the cursor where it was.
                write!(out, "\x1b7\x1b[{};{}H", area.y + 1, area.x + 1)?;
                let chunks: Vec<&[u8]> = png.as_bytes().chunks(CHUNK).collect();
                for (i, chunk) in chunks.iter().enumerate() {
                    let more = u8::from(i + 1 < chunks.len());
                    if i == 0 {
                        write!(
                            out,
                            "\x1b_Ga=T,f=100,i={IMAGE_ID},p={PLACEMENT_ID},q=2,C=1,c={},r={},m={more};",
                            area.width, area.height
                        )?;
                    } else {
                        write!(out, "\x1b_Gm={more};")?;
                    }
                    out.write_all(chunk)?;
                    out.write_all(b"\x1b\\")?;
                }
                write!(out, "\x1b8")?;
                self.current = Some((engine.to_owned(), frame, area));
            }
            None if self.current.is_some() => {
                write!(out, "\x1b_Ga=d,d=I,i={IMAGE_ID},q=2\x1b\\")?;
                self.current = None;
            }
            None => return Ok(()),
        }
        out.flush()
    }
}
