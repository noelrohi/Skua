//! PROTOTYPE (#6): Flash ExternalInterface XML <-> Ruffle ExternalValue,
//! matching what the ActiveX control produces/consumes (FlashUtil.ToFlashXml/FromFlashXml).

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

fn write_value(v: &Value, out: &mut String) {
    match v {
        Value::Undefined => out.push_str("<undefined/>"),
        Value::Null => out.push_str("<null/>"),
        Value::Bool(true) => out.push_str("<true/>"),
        Value::Bool(false) => out.push_str("<false/>"),
        Value::Number(n) => {
            let _ = write!(out, "<number>{n}</number>");
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

pub fn value(v: &Value) -> String {
    let mut s = String::new();
    write_value(v, &mut s);
    s
}

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

fn read_value(n: roxmltree::Node) -> Value {
    match n.tag_name().name() {
        "number" => Value::Number(n.text().unwrap_or("0").trim().parse().unwrap_or(0.0)),
        "true" => Value::Bool(true),
        "false" => Value::Bool(false),
        "null" => Value::Null,
        "undefined" => Value::Undefined,
        "array" => Value::List(
            n.children()
                .filter(|c| c.is_element())
                .map(|p| p.children().find(|c| c.is_element()).map(read_value).unwrap_or(Value::Undefined))
                .collect(),
        ),
        "object" => {
            let mut m = BTreeMap::new();
            for p in n.children().filter(|c| c.is_element()) {
                let k = p.attribute("id").unwrap_or("").to_string();
                let v = p.children().find(|c| c.is_element()).map(read_value).unwrap_or(Value::Undefined);
                m.insert(k, v);
            }
            Value::Object(m)
        }
        _ => Value::String(n.text().unwrap_or("").to_string()),
    }
}

pub fn parse_invoke(xml: &str) -> Result<(String, Vec<Value>), String> {
    let doc = roxmltree::Document::parse(xml).map_err(|e| e.to_string())?;
    let root = doc.root_element();
    let name = root.attribute("name").ok_or("no name")?.to_string();
    let mut args = vec![];
    if let Some(a) = root.children().find(|c| c.has_tag_name("arguments")) {
        for c in a.children().filter(|c| c.is_element()) {
            args.push(read_value(c));
        }
    }
    Ok((name, args))
}
