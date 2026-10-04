use ratatui::layout::Rect;
use skua_tui::picture::Shown;

fn sync(shown: &mut Shown, want: Option<(&str, i64, &str, Rect)>) -> String {
    let mut out = Vec::new();
    shown.sync(&mut out, want).unwrap();
    String::from_utf8(out).unwrap()
}

#[test]
fn a_picture_is_sent_in_chunks_at_its_place_once_per_frame_and_taken_away_when_hidden() {
    let mut shown = Shown::default();
    let area = Rect::new(40, 3, 80, 23);
    let png = "A".repeat(5000);

    let first = sync(&mut shown, Some(("alice", 7, &png, area)));
    let again = sync(&mut shown, Some(("alice", 7, &png, area)));
    let next = sync(&mut shown, Some(("alice", 8, "BBBB", area)));
    let hidden = sync(&mut shown, None);
    let still = sync(&mut shown, None);

    // Into the area's cells, from its top left (1-based), as a PNG; 5000 bytes go in a chunk of 4096 and one of the rest.
    assert!(first.contains("\x1b[4;41H"), "{first:?}");
    assert!(
        first.contains("a=T,f=100,i=7717,p=1,q=2,C=1,c=80,r=23,m=1;"),
        "{first:?}"
    );
    assert!(
        first.contains(&format!("\x1b_Gm=0;{}\x1b\\", "A".repeat(904))),
        "{first:?}"
    );
    assert_eq!(again, "");
    // A new frame replaces the picture in place rather than taking it away first.
    assert!(!next.contains("a=d") && next.contains("m=0;BBBB"), "{next:?}");
    assert_eq!(hidden, "\x1b_Ga=d,d=I,i=7717,q=2\x1b\\");
    assert_eq!(still, "");
}
