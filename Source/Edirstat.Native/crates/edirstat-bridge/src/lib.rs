//! Windows x64 C ABI. Handles and buffers must be released by this library;
//! callers serialize scan lifetime and snapshot calls. No GUI dependency.
use edirstat_core::{
    arena::{FileArenaSnapshot, NO_INDEX},
    state::SharedState,
};
use serde_json::{Value, json};
mod allocation;
mod presentation;
use std::{
    path::PathBuf,
    ptr, slice, str,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
};

pub use edirstat_core::{arena, error::EdirstatError, extensions, fs_utils};
// Scanner modules imported from eDirStat; this workspace has no GUI dependency.
#[path = "engine/coordinator.rs"]
pub mod coordinator;
#[path = "engine/mft.rs"]
pub mod mft;
#[path = "engine/mounts.rs"]
pub mod mounts;
#[path = "engine/traversal.rs"]
pub mod traversal;
pub mod model {
    pub use edirstat_core::arena;
}

#[repr(C)]
pub struct Buffer {
    pub data: *mut u8,
    pub len: usize,
}
impl Buffer {
    fn json(value: Value) -> Self {
        Self::serialized(&value)
    }
    fn serialized(value: &impl serde::Serialize) -> Self {
        let bytes = match serde_json::to_vec(value) {
            Ok(bytes) => bytes.into_boxed_slice(),
            Err(reason) => return Self::error(reason),
        };
        let len = bytes.len();
        Self {
            data: Box::into_raw(bytes).cast::<u8>(),
            len,
        }
    }
    fn error(message: impl ToString) -> Self {
        Self::json(json!({"error": message.to_string()}))
    }
}

pub struct Scan {
    state: Arc<SharedState>,
    done: Arc<AtomicBool>,
    error: Arc<Mutex<Option<String>>>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Drop for Scan {
    fn drop(&mut self) {
        self.state.scan_cancel.store(true, Ordering::SeqCst);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

pub struct Snapshot {
    tree: Arc<FileArenaSnapshot>,
    // Index one viewed directory at a time, never marshal the entire tree.
    children: Mutex<(u32, u32, bool, Vec<u32>)>,
    extensions: Arc<Vec<(compact_str::CompactString, u64, u32)>>,
    allocation: Mutex<Option<Arc<allocation::Job>>>,
}
impl Drop for Snapshot {
    fn drop(&mut self) {
        allocation::cancel(self);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn edirstat_abi_version() -> u32 {
    4
}

/// UTF-8 input of `len` bytes; `output` must point to writable pointer storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_scan_start(
    path: *const u8,
    len: usize,
    output: *mut *mut Scan,
) -> Buffer {
    if output.is_null() {
        return Buffer::error("Missing output handle");
    }
    unsafe {
        *output = ptr::null_mut();
    }
    if path.is_null() || len == 0 || len > 32768 {
        return Buffer::error("Invalid path");
    }
    let text = match str::from_utf8(unsafe { slice::from_raw_parts(path, len) }) {
        Ok(text) => text.to_owned(),
        Err(e) => return Buffer::error(e),
    };
    let root = PathBuf::from(&text);
    // Reject invalid roots before starting; the walker reports inaccessible children separately.
    if let Err(e) = std::fs::read_dir(&root) {
        return Buffer::error(e);
    }
    let state = Arc::new(SharedState::new());
    let done = Arc::new(AtomicBool::new(false));
    let error = Arc::new(Mutex::new(None));
    let (s, d, e) = (state.clone(), done.clone(), error.clone());
    let worker = match thread::Builder::new()
        .name("edirstat-scan".into())
        .spawn(move || {
            let (tx, rx) = crossbeam::channel::bounded(64);
            let engine = traversal::TraversalEngine::new(s.scan_stats.clone());
            match engine.start_traversal(root, true, s.scan_cancel.clone(), tx) {
                Ok(traversal) => {
                    coordinator::Coordinator::new(rx, s.clone()).run_coordinator_loop(&text);
                    if traversal.join().is_err() {
                        *e.lock().unwrap_or_else(|p| p.into_inner()) =
                            Some("Scan worker failed".into());
                    }
                }
                Err(reason) => {
                    *e.lock().unwrap_or_else(|p| p.into_inner()) = Some(reason.to_string());
                }
            }
            d.store(true, Ordering::Release);
        }) {
        Ok(worker) => worker,
        Err(e) => return Buffer::error(e),
    };
    unsafe {
        *output = Box::into_raw(Box::new(Scan {
            state,
            done,
            error,
            worker: Some(worker),
        }));
    }
    Buffer::json(json!({"error": null}))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_scan_state(scan: *const Scan) -> Buffer {
    let Some(scan) = (unsafe { scan.as_ref() }) else {
        return Buffer::error("Invalid scan");
    };
    let stats = &scan.state.scan_stats;
    Buffer::json(json!({
        "done": scan.done.load(Ordering::Acquire),
        "cancelled": scan.state.scan_cancel.load(Ordering::SeqCst),
        "files": stats.files_scanned.load(Ordering::Relaxed),
        "directories": stats.dirs_scanned.load(Ordering::Relaxed),
        "bytes": stats.bytes_scanned.load(Ordering::Relaxed),
        "error": *scan.error.lock().unwrap_or_else(|p| p.into_inner()),
    }))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_scan_cancel(scan: *const Scan) {
    if let Some(scan) = unsafe { scan.as_ref() } {
        scan.state.scan_cancel.store(true, Ordering::SeqCst);
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_scan_release(scan: *mut Scan) {
    if !scan.is_null() {
        drop(unsafe { Box::from_raw(scan) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_acquire(scan: *const Scan) -> *mut Snapshot {
    let Some(scan) = (unsafe { scan.as_ref() }) else {
        return ptr::null_mut();
    };
    // Only acquire the completed result: no repeated directory sorts or retained live copies.
    if !scan.done.load(Ordering::Acquire) || scan.state.scan_cancel.load(Ordering::SeqCst) {
        return ptr::null_mut();
    }
    Box::into_raw(Box::new(Snapshot {
        tree: scan.state.current_snapshot.load_full(),
        children: Mutex::new((NO_INDEX, 0, true, Vec::new())),
        extensions: scan.state.extension_stats.load_full(),
        allocation: Mutex::new(None),
    }))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_page(
    snapshot: *const Snapshot,
    parent: u32,
    offset: u32,
    count: u32,
    sort: u32,
    descending: u32,
) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    let tree = &snapshot.tree;
    let Some(node) = tree.nodes.get(parent as usize) else {
        return Buffer::error("Unknown directory");
    };
    if !node.is_directory() {
        return Buffer::error("Not a directory");
    }
    let mut cache = snapshot.children.lock().unwrap_or_else(|p| p.into_inner());
    if cache.0 != parent || cache.1 != sort || cache.2 != (descending != 0) {
        cache.3.clear();
        let mut child = node.first_child;
        while child != NO_INDEX {
            let Some(item) = tree.nodes.get(child as usize) else {
                return Buffer::error("Invalid child index");
            };
            cache.3.push(child);
            child = item.next_sibling;
        }
        cache
            .3
            .sort_unstable_by(|a, b| presentation::compare(tree, *a, *b, sort, descending != 0));
        cache.0 = parent;
        cache.1 = sort;
        cache.2 = descending != 0;
    }
    let items: Vec<Value> = cache
        .3
        .iter()
        .skip(offset as usize)
        .take(count.clamp(1, 512) as usize)
        .map(|&id| presentation::entry(snapshot, id))
        .collect();
    let listed: u64 = items.iter().map(|v| v["size"].as_u64().unwrap_or(0)).sum();
    Buffer::json(
        json!({"error": null, "id": parent, "parent": node.parent, "path": tree.get_full_path(parent),
        "size": node.size, "denied": node.has_no_permission(), "total": cache.3.len(), "offset": offset,
        "otherSize": node.size.saturating_sub(listed), "items": items}),
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_release(snapshot: *mut Snapshot) {
    if !snapshot.is_null() {
        drop(unsafe { Box::from_raw(snapshot) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_buffer_release(buffer: Buffer) {
    if !buffer.data.is_null() {
        drop(unsafe { Box::from_raw(ptr::slice_from_raw_parts_mut(buffer.data, buffer.len)) });
    }
}
