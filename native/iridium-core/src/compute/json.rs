//! JSON parser producing a flat SoA DOM for the Mono side.
//!
//! Output semantics mirror GDMiniJSON (the game's parser) so the host can
//! materialize the exact same `Dictionary<string, object>` / `List<object>` /
//! `string` / `int` / `float` / `bool` / `null` graph:
//! - A number token containing '.' becomes f32, otherwise i32.
//! - .NET style parse failure (TryParse returning false) yields 0, not an
//!   error; overflow therefore also yields 0.
//! - Strings are interned: identical content shares one pool slice, which
//!   lets the host cache managed strings by pool offset.
//! - Anything structurally malformed returns Err so the host falls back to
//!   GDMiniJSON, preserving its lenient behavior on broken files.
//!
//! Input is UTF-16 (the C# string's code units); escape decoding emits UTF-16
//! units directly, so surrogate pairs pass through untouched.

use std::collections::HashMap;

pub const KIND_NULL: u8 = 0;
pub const KIND_FALSE: u8 = 1;
pub const KIND_TRUE: u8 = 2;
pub const KIND_INT: u8 = 3;
pub const KIND_FLOAT: u8 = 4;
pub const KIND_STRING: u8 = 5;
pub const KIND_ARRAY: u8 = 6;
pub const KIND_OBJECT: u8 = 7;

/// Nesting cap; deeper documents fall back to the managed parser instead of
/// risking a stack overflow in the native guard.
const MAX_DEPTH: u32 = 128;
/// Strings longer than this are not interned (they are usually unique).
const INTERN_MAX_LEN: usize = 128;

/// Flat DOM. Parallel arrays: node `i` is `KIND_*` at `kinds[i]` with payload
/// - int: `a[i]` is the value bits
/// - float: `values[i]`
/// - string: `a[i]` is an interned string id
/// - array: `a[i]`/`b[i]` are offset/count into `children`
/// - object: same as array, but children come in (key_node, value_node) pairs
///
/// String table: id -> (`str_off[id]`, `str_len[id]`) into the UTF-16 `strings`
/// pool. Identical content always maps to the same id, so the host can cache
/// managed strings by id.
#[derive(Default)]
pub struct Doc {
    pub kinds: Vec<u8>,
    pub a: Vec<u32>,
    pub b: Vec<u32>,
    pub values: Vec<f32>,
    pub children: Vec<u32>,
    pub strings: Vec<u16>,
    pub str_off: Vec<u32>,
    pub str_len: Vec<u32>,
    pub root: u32,
    intern: HashMap<Box<[u16]>, u32>,
    scratch: Vec<u16>,
    /// Stack used to keep each container's child list contiguous: nested
    /// containers append to `children` when they close, before the parent.
    pending: Vec<u32>,
}

impl Doc {
    pub fn reset(&mut self) {
        self.kinds.clear();
        self.a.clear();
        self.b.clear();
        self.values.clear();
        self.children.clear();
        self.strings.clear();
        self.str_off.clear();
        self.str_len.clear();
        self.intern.clear();
        self.scratch.clear();
        self.pending.clear();
        self.root = 0;
    }
}

/// Parses `text` into `doc` (which is reset first).
pub fn parse(text: &[u16], doc: &mut Doc) -> Result<(), ()> {
    doc.reset();
    let mut p = Parser { s: text, i: 0, section: None };
    p.skip_ws();
    if p.i >= p.s.len() {
        // GDMiniJSON returns null for empty input.
        doc.root = push(doc, KIND_NULL, 0, 0, 0.0);
        return Ok(());
    }
    let root = p.parse_value(doc, 0)?;
    doc.root = root;
    Ok(())
}

/// Like `parse`, but stops when the root object's next key equals `section`
/// (GDMiniJSON `DeserializePartially`); the key is left unconsumed and the
/// object parsed so far is returned. If the key never appears at the root,
/// the full document is parsed.
pub fn parse_with_section(text: &[u16], section: &[u16], doc: &mut Doc) -> Result<(), ()> {
    doc.reset();
    let mut p = Parser {
        s: text,
        i: 0,
        section: Some(section),
    };
    p.skip_ws();
    if p.i >= p.s.len() {
        doc.root = push(doc, KIND_NULL, 0, 0, 0.0);
        return Ok(());
    }
    let root = p.parse_value(doc, 0)?;
    doc.root = root;
    Ok(())
}

struct Parser<'a> {
    s: &'a [u16],
    i: usize,
    section: Option<&'a [u16]>,
}

impl<'a> Parser<'a> {
    fn skip_ws(&mut self) {
        while self.i < self.s.len() {
            match self.s[self.i] {
                0x20 | 0x09 | 0x0A | 0x0D | 0xFEFF => self.i += 1,
                _ => break,
            }
        }
    }

    fn parse_value(&mut self, doc: &mut Doc, depth: u32) -> Result<u32, ()> {
        if depth > MAX_DEPTH {
            return Err(());
        }
        self.skip_ws();
        let c = *self.s.get(self.i).ok_or(())?;
        match c {
            0x7B => self.parse_object(doc, depth), // {
            0x5B => self.parse_array(doc, depth),  // [
            0x22 => self.parse_string_node(doc),   // "
            0x74 => {
                self.expect_word(b"true")?;
                Ok(push(doc, KIND_TRUE, 0, 0, 0.0))
            }
            0x66 => {
                self.expect_word(b"false")?;
                Ok(push(doc, KIND_FALSE, 0, 0, 0.0))
            }
            0x6E => {
                self.expect_word(b"null")?;
                Ok(push(doc, KIND_NULL, 0, 0, 0.0))
            }
            0x2D | 0x30..=0x39 => self.parse_number(doc),
            // Unknown token: GDMiniJSON would yield null (dictionary value)
            // or a partial result; bail so the managed parser handles it.
            _ => Err(()),
        }
    }

    fn parse_object(&mut self, doc: &mut Doc, depth: u32) -> Result<u32, ()> {
        self.i += 1; // '{'
        let node = push(doc, KIND_OBJECT, 0, 0, 0.0);
        let pending_start = doc.pending.len();
        let mut pairs: u32 = 0;
        loop {
            self.skip_ws();
            match self.s.get(self.i) {
                None => return Err(()),
                Some(0x7D) => {
                    self.i += 1; // '}'
                    break;
                }
                Some(0x2C) => {
                    self.i += 1; // stray comma (MiniJSON tolerates)
                    continue;
                }
                _ => {}
            }
            let key = self.parse_string_node(doc)?;
            self.skip_ws();
            if self.s.get(self.i) != Some(&0x3A) {
                return Err(()); // ':'
            }
            // DeserializePartially: stop before consuming ':'/value when the
            // key matches, returning the object parsed so far (MiniJSON
            // checks this in every object, root included).
            if let Some(section) = self.section {
                let id = doc.a[key as usize] as usize;
                let (off, len) = (doc.str_off[id] as usize, doc.str_len[id] as usize);
                if &doc.strings[off..off + len] == section {
                    break;
                }
            }
            self.i += 1;
            let value = self.parse_value(doc, depth + 1)?;
            doc.pending.push(key);
            doc.pending.push(value);
            pairs += 1;
        }
        // Commit the (contiguous) child list. Nested containers already
        // committed theirs while closing, so slicing from pending keeps each
        // container's range self-contained.
        let child_start = doc.children.len() as u32;
        doc.children.extend_from_slice(&doc.pending[pending_start..]);
        doc.pending.truncate(pending_start);
        doc.a[node as usize] = child_start;
        doc.b[node as usize] = pairs;
        Ok(node)
    }

    fn parse_array(&mut self, doc: &mut Doc, depth: u32) -> Result<u32, ()> {
        self.i += 1; // '['
        let node = push(doc, KIND_ARRAY, 0, 0, 0.0);
        let pending_start = doc.pending.len();
        let mut count: u32 = 0;
        loop {
            self.skip_ws();
            match self.s.get(self.i) {
                None => return Err(()),
                Some(0x5D) => {
                    self.i += 1; // ']'
                    break;
                }
                Some(0x2C) => {
                    self.i += 1;
                    continue;
                }
                _ => {}
            }
            let value = self.parse_value(doc, depth + 1)?;
            doc.pending.push(value);
            count += 1;
        }
        let child_start = doc.children.len() as u32;
        doc.children.extend_from_slice(&doc.pending[pending_start..]);
        doc.pending.truncate(pending_start);
        doc.a[node as usize] = child_start;
        doc.b[node as usize] = count;
        Ok(node)
    }

    fn parse_number(&mut self, doc: &mut Doc) -> Result<u32, ()> {
        let start = self.i;
        while self.i < self.s.len() && !is_word_break(self.s[self.i]) {
            self.i += 1;
        }
        let w = &self.s[start..self.i];
        if w.is_empty() {
            return Err(());
        }
        if w.iter().any(|&c| c == 0x2E) {
            // '.' present: float.TryParse semantics (failure => 0).
            Ok(push(doc, KIND_FLOAT, 0, 0, ascii_to_f32(w)))
        } else {
            // no '.': int.TryParse semantics (failure/overflow => 0).
            Ok(push(doc, KIND_INT, ascii_to_i32(w) as u32, 0, 0.0))
        }
    }

    fn parse_string_node(&mut self, doc: &mut Doc) -> Result<u32, ()> {
        let id = self.parse_string(doc)?;
        Ok(push(doc, KIND_STRING, id, 0, 0.0))
    }

    fn parse_string(&mut self, doc: &mut Doc) -> Result<u32, ()> {
        if self.s.get(self.i) != Some(&0x22) {
            return Err(());
        }
        self.i += 1;
        let start = self.i;
        // Fast path: no escapes, straight slice.
        while self.i < self.s.len() {
            let c = self.s[self.i];
            if c == 0x22 {
                let end = self.i;
                self.i += 1;
                return Ok(intern_string(doc, &self.s[start..end]));
            }
            if c == 0x5C {
                break;
            }
            self.i += 1;
        }
        if self.i >= self.s.len() {
            return Err(());
        }

        // Slow path: decode escapes into reusable scratch.
        let mut buf = std::mem::take(&mut doc.scratch);
        buf.clear();
        buf.extend_from_slice(&self.s[start..self.i]);
        let result = loop {
            if self.i >= self.s.len() {
                break Err(());
            }
            let c = self.s[self.i];
            self.i += 1;
            if c == 0x22 {
                break Ok(intern_string(doc, &buf));
            }
            if c != 0x5C {
                buf.push(c);
                continue;
            }
            if self.i >= self.s.len() {
                break Err(());
            }
            let e = self.s[self.i];
            self.i += 1;
            match e {
                0x22 | 0x2F | 0x5C => buf.push(e),
                0x62 => buf.push(0x08),
                0x66 => buf.push(0x0C),
                0x6E => buf.push(0x0A),
                0x72 => buf.push(0x0D),
                0x74 => buf.push(0x09),
                0x75 => {
                    if self.i + 4 > self.s.len() {
                        break Err(());
                    }
                    let mut v: u32 = 0;
                    let mut ok = true;
                    for k in 0..4 {
                        let h = self.s[self.i + k];
                        let d = match h {
                            0x30..=0x39 => h - 0x30,
                            0x41..=0x46 => h - 0x41 + 10,
                            0x61..=0x66 => h - 0x61 + 10,
                            _ => {
                                ok = false;
                                0
                            }
                        };
                        v = v * 16 + d as u32;
                    }
                    self.i += 4;
                    if !ok {
                        break Err(());
                    }
                    buf.push(v as u16);
                }
                // Unknown escape: MiniJSON silently drops it.
                _ => {}
            }
        };
        doc.scratch = buf;
        result
    }

    fn expect_word(&mut self, expect: &[u8]) -> Result<(), ()> {
        let start = self.i;
        while self.i < self.s.len() && !is_word_break(self.s[self.i]) {
            self.i += 1;
        }
        let w = &self.s[start..self.i];
        if w.len() == expect.len() && w.iter().zip(expect).all(|(&u, &b)| u == b as u16) {
            Ok(())
        } else {
            Err(())
        }
    }
}

#[inline]
fn push(doc: &mut Doc, kind: u8, a: u32, b: u32, f: f32) -> u32 {
    let idx = doc.kinds.len() as u32;
    doc.kinds.push(kind);
    doc.a.push(a);
    doc.b.push(b);
    doc.values.push(f);
    idx
}

/// MiniJSON word-break set: `" \t\n\r{}[],:\""`.
#[inline]
fn is_word_break(c: u16) -> bool {
    matches!(
        c,
        0x20 | 0x09 | 0x0A | 0x0D | 0x7B | 0x7D | 0x5B | 0x5D | 0x2C | 0x3A | 0x22
    )
}

fn intern_string(doc: &mut Doc, slice: &[u16]) -> u32 {
    let Doc {
        strings,
        str_off,
        str_len,
        intern,
        ..
    } = doc;
    if slice.len() <= INTERN_MAX_LEN {
        if let Some(&id) = intern.get(slice) {
            return id;
        }
        let off = strings.len() as u32;
        strings.extend_from_slice(slice);
        let id = str_off.len() as u32;
        str_off.push(off);
        str_len.push(slice.len() as u32);
        intern.insert(slice.to_vec().into_boxed_slice(), id);
        id
    } else {
        let off = strings.len() as u32;
        strings.extend_from_slice(slice);
        let id = str_off.len() as u32;
        str_off.push(off);
        str_len.push(slice.len() as u32);
        id
    }
}

/// .NET `int.TryParse` approximation: failure (including overflow) => 0.
fn ascii_to_i32(w: &[u16]) -> i32 {
    if w.len() > 24 {
        return 0;
    }
    let mut buf = [0u8; 24];
    for (k, &c) in w.iter().enumerate() {
        if c > 0x7F {
            return 0;
        }
        buf[k] = c as u8;
    }
    match std::str::from_utf8(&buf[..w.len()]) {
        Ok(s) => s.parse::<i32>().unwrap_or(0),
        Err(_) => 0,
    }
}

/// .NET `float.TryParse` approximation: failure => 0.
fn ascii_to_f32(w: &[u16]) -> f32 {
    if w.len() > 48 {
        return 0.0;
    }
    let mut buf = [0u8; 48];
    for (k, &c) in w.iter().enumerate() {
        if c > 0x7F {
            return 0.0;
        }
        buf[k] = c as u8;
    }
    match std::str::from_utf8(&buf[..w.len()]) {
        Ok(s) => s.parse::<f32>().unwrap_or(0.0),
        Err(_) => 0.0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn utf16(s: &str) -> Vec<u16> {
        s.encode_utf16().collect()
    }

    fn parse_str(s: &str) -> Doc {
        let mut doc = Doc::default();
        parse(&utf16(s), &mut doc).expect("parse failed");
        doc
    }

    #[test]
    fn scalars_and_types() {
        let doc = parse_str("{\"i\": 42, \"f\": 1.5, \"b\": true, \"n\": null, \"s\": \"hi\"}");
        assert_eq!(doc.kinds[0], KIND_OBJECT);
        assert_eq!(doc.b[0], 5);
        // child key/value pairs
        let kids: Vec<u32> = doc.children.clone();
        let values = [
            (KIND_INT, 42i32 as f32),
            (KIND_FLOAT, 1.5),
            (KIND_TRUE, 0.0),
            (KIND_NULL, 0.0),
        ];
        for (idx, (kind, _)) in values.iter().enumerate() {
            let v = kids[idx * 2 + 1] as usize;
            assert_eq!(doc.kinds[v], *kind);
        }
        let v = kids[1] as usize;
        assert_eq!(doc.a[v] as i32, 42);
        let v = kids[3] as usize;
        assert_eq!(doc.values[v], 1.5);
        let v = kids[9] as usize;
        assert_eq!(doc.kinds[v], KIND_STRING);
        let id = doc.a[v] as usize;
        let (off, len) = (doc.str_off[id] as usize, doc.str_len[id] as usize);
        assert_eq!(String::from_utf16(&doc.strings[off..off + len]).unwrap(), "hi");
    }

    #[test]
    fn strings_are_interned() {
        let doc = parse_str("{\"k\": 1, \"k2\": 1, \"k\": 2}");
        // last key wins on the host side; parser emits both pairs but the
        // identical "k" strings must share one interned id.
        let kids = doc.children.clone();
        let key0 = kids[0] as usize;
        let key3 = kids[4] as usize;
        assert_eq!(doc.a[key0], doc.a[key3]);
    }

    #[test]
    fn escapes() {
        let doc = parse_str("\"a\\n\\u0041\\\\\\\"\"");
        let n = doc.root as usize;
        let id = doc.a[n] as usize;
        let (off, len) = (doc.str_off[id] as usize, doc.str_len[id] as usize);
        let s = String::from_utf16(&doc.strings[off..off + len]).unwrap();
        assert_eq!(s, "a\nA\\\"");
    }

    #[test]
    fn number_failure_yields_zero() {
        let doc = parse_str("[9999999999999, 1e5, 1.5e3, 1.]");
        let base = doc.a[0] as usize;
        let kids = &doc.children[base..base + 4];
        let k0 = doc.kinds[kids[0] as usize];
        assert_eq!(k0, KIND_INT);
        assert_eq!(doc.a[kids[0] as usize] as i32, 0); // overflow => 0
        assert_eq!(doc.a[kids[1] as usize] as i32, 0); // 1e5 has no '.' => int parse fails
        assert_eq!(doc.values[kids[2] as usize], 1500.0);
        assert_eq!(doc.values[kids[3] as usize], 1.0);
    }

    #[test]
    fn malformed_errors() {
        let mut doc = Doc::default();
        assert!(parse(&utf16("[1, 2"), &mut doc).is_err());
        assert!(parse(&utf16("tru"), &mut doc).is_err());
        assert!(parse(&utf16("{a:1}"), &mut doc).is_err());
        assert!(parse(&utf16(".5"), &mut doc).is_err());
    }

    #[test]
    fn nesting_and_arrays() {
        let doc = parse_str("{\"a\": [1, [2, {\"b\": \"c\"}]]}");
        let root = doc.root as usize;
        assert_eq!(doc.kinds[root], KIND_OBJECT);
        // Root's own children start at a[root].
        let arr = doc.children[doc.a[root] as usize + 1] as usize;
        assert_eq!(doc.kinds[arr], KIND_ARRAY);
        assert_eq!(doc.b[arr], 2);
        // First element of the outer array is an int.
        let first = doc.children[doc.a[arr] as usize] as usize;
        assert_eq!(doc.kinds[first], KIND_INT);
        // Second element is the nested array with 2 children.
        let nested = doc.children[doc.a[arr] as usize + 1] as usize;
        assert_eq!(doc.kinds[nested], KIND_ARRAY);
        assert_eq!(doc.b[nested], 2);
    }

    #[test]
    fn partial_parse_stops_at_section() {
        let text = utf16("{\"settings\": {\"song\": \"s\"}, \"actions\": [1,2], \"x\": 3}");
        let mut doc = Doc::default();
        parse_with_section(&text, &utf16("actions"), &mut doc).unwrap();
        let root = doc.root as usize;
        assert_eq!(doc.kinds[root], KIND_OBJECT);
        assert_eq!(doc.b[root], 1); // only "settings" pair
    }
}
