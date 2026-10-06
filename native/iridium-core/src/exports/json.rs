//! JSON parse exports (flat DOM view).
//!
//! Thread model: one parser document per thread. The host calls
//! `json_parse` -> `json_view` -> (materializes) -> `json_release` on the
//! same thread. Pointers in `JsonView` stay valid until `json_release` or
//! the next `json_parse` on that thread.

use crate::compute::json::{self, Doc};
use crate::ffi;
use std::cell::RefCell;

thread_local! {
    static DOC: RefCell<Doc> = RefCell::new(Doc::default());
}

/// Parses UTF-16 JSON text into the thread-local DOM.
///
/// `section` (optional, may be null) mirrors `GDMiniJSON.Json.DeserializePartially`:
/// parsing stops before the root object's key equal to `section`.
///
/// Returns 0 on success, negative `ffi::ERR_*` on failure. On `ERR_PARSE` the
/// host must fall back to the managed parser (malformed input semantics).
#[no_mangle]
pub unsafe extern "C" fn iridium_core_json_parse(
    text: *const u16,
    len: u32,
    section: *const u16,
    section_len: u32,
) -> i32 {
    crate::guard(|| {
        let text = match ffi::slice(text, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let section = ffi::slice(section, section_len);

        DOC.with(|cell| {
            let mut doc = cell.borrow_mut();
            let result = match section {
                Some(sec) => json::parse_with_section(text, sec, &mut doc),
                None => json::parse(text, &mut doc),
            };
            match result {
                Ok(()) => ffi::ERR_OK,
                Err(()) => {
                    doc.reset();
                    ffi::ERR_PARSE
                }
            }
        })
    })
}

/// Fills `view` with pointers into the current thread's DOM.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_json_view(view: *mut ffi::JsonView) -> i32 {
    crate::guard(|| {
        if view.is_null() {
            return ffi::ERR_NULL_ARG;
        }
        DOC.with(|cell| {
            let doc = cell.borrow();
            let v = &mut *view;
            v.kinds = doc.kinds.as_ptr();
            v.a = doc.a.as_ptr();
            v.b = doc.b.as_ptr();
            v.values = doc.values.as_ptr();
            v.children = doc.children.as_ptr();
            v.strings = doc.strings.as_ptr();
            v.str_off = doc.str_off.as_ptr();
            v.str_len = doc.str_len.as_ptr();
            v.node_count = doc.kinds.len() as u32;
            v.child_count = doc.children.len() as u32;
            v.string_count = doc.str_off.len() as u32;
            v.string_pool_len = doc.strings.len() as u32;
            v.root = doc.root;
            ffi::ERR_OK
        })
    })
}

/// Drops the DOM and its buffers (call once materialization is done).
#[no_mangle]
pub extern "C" fn iridium_core_json_release() -> i32 {
    crate::guard(|| {
        DOC.with(|cell| {
            *cell.borrow_mut() = Doc::default();
        });
        ffi::ERR_OK
    })
}
