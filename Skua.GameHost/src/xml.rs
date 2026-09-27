//! Flash ExternalInterface XML <-> Ruffle `ExternalValue`, in the format the ActiveX control produces
//! and consumes (`FlashUtil.ToFlashXml` / `FromFlashXml` on the Engine side).

use ruffle_core::external::Value;
use std::collections::BTreeMap;
use std::fmt::Write;

fn escape(s: &str, out: &mut String) {
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&apos;"),
            c => out.push(c),
        }
    }
}

/// Numbers as AS3's `String(n)` writes them where the two differ from Rust.
fn write_number(n: f64, out: &mut String) {
    if n == f64::INFINITY {
        out.push_str("Infinity");
    } else if n == f64::NEG_INFINITY {
        out.push_str("-Infinity");
    } else if n == 0.0 {
        out.push('0');
    } else {
        let _ = write!(out, "{n}");
    }
}

fn write_value(v: &Value, out: &mut String) {
    match v {
        Value::Undefined => out.push_str("<undefined/>"),
        Value::Null => out.push_str("<null/>"),
        Value::Bool(true) => out.push_str("<true/>"),
        Value::Bool(false) => out.push_str("<false/>"),
        Value::Number(n) => {
            out.push_str("<number>");
            write_number(*n, out);
            out.push_str("</number>");
        }
        Value::String(s) => {
            out.push_str("<string>");
            escape(s, out);
            out.push_str("</string>");
        }
        Value::List(items) => {
            out.push_str("<array>");
            for (i, item) in items.iter().enumerate() {
                let _ = write!(out, "<property id=\"{i}\">");
                write_value(item, out);
                out.push_str("</property>");
            }
            out.push_str("</array>");
        }
        Value::Object(map) => {
            out.push_str("<object>");
            for (k, item) in map {
                out.push_str("<property id=\"");
                escape(k, out);
                out.push_str("\">");
                write_value(item, out);
                out.push_str("</property>");
            }
            out.push_str("</object>");
        }
    }
}

/// The return XML of a call ('R').
pub fn value(v: &Value) -> String {
    let mut s = String::new();
    write_value(v, &mut s);
    s
}

/// The `<invoke>` XML of an AS3 `ExternalInterface.call` ('E').
pub fn invoke(name: &str, args: &[Value]) -> String {
    let mut s = String::from("<invoke name=\"");
    escape(name, &mut s);
    s.push_str("\" returntype=\"xml\"><arguments>");
    for a in args {
        write_value(a, &mut s);
    }
    s.push_str("</arguments></invoke>");
    s
}

fn first_element(n: roxmltree::Node) -> Value {
    n.children()
        .find(|c| c.is_element())
        .map(read_value)
        .unwrap_or(Value::Undefined)
}

fn read_value(n: roxmltree::Node) -> Value {
    match n.tag_name().name() {
        "number" => Value::Number(n.text().unwrap_or("0").trim().parse().unwrap_or(0.0)),
        "true" => Value::Bool(true),
        "false" => Value::Bool(false),
        "null" => Value::Null,
        "undefined" => Value::Undefined,
        "array" => Value::List(n.children().filter(|c| c.is_element()).map(first_element).collect()),
        "object" => Value::Object(
            n.children()
                .filter(|c| c.is_element())
                .map(|p| (p.attribute("id").unwrap_or("").to_string(), first_element(p)))
                .collect::<BTreeMap<_, _>>(),
        ),
        _ => Value::String(n.text().unwrap_or("").to_string()),
    }
}

/// Parses the `<invoke>` XML of a call ('C') into the callback name and its arguments.
pub fn parse_invoke(xml: &str) -> Result<(String, Vec<Value>), String> {
    let doc = roxmltree::Document::parse(xml).map_err(|e| e.to_string())?;
    let root = doc.root_element();
    if !root.has_tag_name("invoke") {
        return Err(format!("expected <invoke>, got <{}>", root.tag_name().name()));
    }
    let name = root.attribute("name").ok_or("<invoke> without a name")?.to_string();
    let args = match root.children().find(|c| c.has_tag_name("arguments")) {
        Some(a) => a.children().filter(|c| c.is_element()).map(read_value).collect(),
        None => vec![],
    };
    Ok((name, args))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn object(pairs: &[(&str, Value)]) -> Value {
        Value::Object(pairs.iter().map(|(k, v)| (k.to_string(), v.clone())).collect())
    }

    #[test]
    fn writes_scalars() {
        assert_eq!(value(&Value::Undefined), "<undefined/>");
        assert_eq!(value(&Value::Null), "<null/>");
        assert_eq!(value(&Value::Bool(true)), "<true/>");
        assert_eq!(value(&Value::Bool(false)), "<false/>");
        assert_eq!(value(&Value::String("Init".into())), "<string>Init</string>");
    }

    #[test]
    fn writes_numbers_like_as3() {
        assert_eq!(value(&Value::Number(3.0)), "<number>3</number>");
        assert_eq!(value(&Value::Number(-1.5)), "<number>-1.5</number>");
        assert_eq!(value(&Value::Number(-0.0)), "<number>0</number>");
        assert_eq!(value(&Value::Number(f64::NAN)), "<number>NaN</number>");
        assert_eq!(value(&Value::Number(f64::INFINITY)), "<number>Infinity</number>");
        assert_eq!(value(&Value::Number(f64::NEG_INFINITY)), "<number>-Infinity</number>");
    }

    #[test]
    fn escapes_strings_and_names() {
        assert_eq!(
            value(&Value::String(r#"<a b="c">&'"#.into())),
            "<string>&lt;a b=&quot;c&quot;&gt;&amp;&apos;</string>"
        );
        assert_eq!(
            invoke("a&b", &[]),
            r#"<invoke name="a&amp;b" returntype="xml"><arguments></arguments></invoke>"#
        );
    }

    #[test]
    fn writes_arrays_and_objects() {
        let v = Value::List(vec![
            Value::Number(1.0),
            object(&[("b", Value::Null), ("a", Value::Bool(true))]),
        ]);
        assert_eq!(
            value(&v),
            r#"<array><property id="0"><number>1</number></property><property id="1"><object><property id="a"><true/></property><property id="b"><null/></property></object></property></array>"#
        );
    }

    #[test]
    fn writes_an_event_invoke() {
        let json = r#"{"t":"xt","b":{"r":-1}}"#;
        assert_eq!(
            invoke("pext", &[Value::String(json.into())]),
            r#"<invoke name="pext" returntype="xml"><arguments><string>{&quot;t&quot;:&quot;xt&quot;,&quot;b&quot;:{&quot;r&quot;:-1}}</string></arguments></invoke>"#
        );
    }

    #[test]
    fn parses_a_call_without_arguments() {
        // FlashUtil.Call leaves <arguments> out when there are none.
        assert_eq!(
            parse_invoke(r#"<invoke name="loadClient" returntype="xml"></invoke>"#),
            Ok(("loadClient".into(), vec![]))
        );
        assert_eq!(
            parse_invoke(r#"<invoke name="x" returntype="xml"><arguments></arguments></invoke>"#),
            Ok(("x".into(), vec![]))
        );
    }

    #[test]
    fn parses_call_arguments_as_flashutil_writes_them() {
        let xml = concat!(
            r#"<invoke name="callGameFunction" returntype="xml"><arguments>"#,
            r#"<string>world.moveToCell</string><number>12</number><number>0.25</number><true/><false/><null/>"#,
            r#"<string>a &amp; &lt;b&gt;</string><string></string>"#,
            r#"<array><property id="0"><number>1</number></property><property id="1"><string>x</string></property></array>"#,
            r#"<object><property id="k"><object><property id="n"><undefined/></property></object></property></object>"#,
            r#"</arguments></invoke>"#
        );
        let (name, args) = parse_invoke(xml).unwrap();
        assert_eq!(name, "callGameFunction");
        assert_eq!(
            args,
            vec![
                Value::String("world.moveToCell".into()),
                Value::Number(12.0),
                Value::Number(0.25),
                Value::Bool(true),
                Value::Bool(false),
                Value::Null,
                Value::String("a & <b>".into()),
                Value::String("".into()),
                Value::List(vec![Value::Number(1.0), Value::String("x".into())]),
                object(&[("k", object(&[("n", Value::Undefined)]))]),
            ]
        );
    }

    #[test]
    fn written_values_parse_back() {
        let v = vec![
            Value::Number(-7.125),
            Value::Number(f64::INFINITY),
            Value::String("<'&\">".into()),
            Value::List(vec![Value::Null, Value::List(vec![])]),
            object(&[("a b", Value::Bool(false)), ("", Value::String("e".into()))]),
        ];
        assert_eq!(parse_invoke(&invoke("f", &v)), Ok(("f".into(), v)));
    }

    #[test]
    fn a_bad_number_reads_as_zero() {
        assert_eq!(
            parse_invoke("<invoke name=\"f\"><arguments><number>1,5</number></arguments></invoke>")
                .unwrap()
                .1,
            vec![Value::Number(0.0)]
        );
    }

    #[test]
    fn rejects_malformed_invokes() {
        assert!(parse_invoke("").is_err());
        assert!(parse_invoke("<invoke name=\"x\">").is_err());
        assert!(parse_invoke("<invoke><arguments/></invoke>").is_err());
        assert!(parse_invoke("<call name=\"x\"/>").is_err());
    }
}
