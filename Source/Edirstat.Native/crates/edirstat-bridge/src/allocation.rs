//! Optional allocation metadata pass. It never adds work to the normal scanner.
use crate::{
    Buffer, Snapshot,
    arena::{FileArenaSnapshot, NO_INDEX},
};
use rayon::prelude::*;
use serde_json::json;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicU64, Ordering},
};

pub struct Data {
    pub sizes: Vec<u64>,
    pub incomplete: Vec<bool>,
}
pub struct Job {
    pub cancel: AtomicBool,
    done: AtomicBool,
    processed: AtomicU64,
    failed: AtomicU64,
    total: u64,
    data: Mutex<Option<Arc<Data>>>,
    error: Mutex<Option<String>>,
}
impl Job {
    fn state(&self) -> Buffer {
        Buffer::json(json!({"done":self.done.load(Ordering::Acquire),
            "cancelled":self.cancel.load(Ordering::Relaxed),
            "processed":self.processed.load(Ordering::Relaxed),"total":self.total,
            "failed":self.failed.load(Ordering::Relaxed),
            "error":*self.error.lock().unwrap_or_else(|p|p.into_inner())}))
    }
    fn run(&self, tree: &FileArenaSnapshot) -> Result<(), String> {
        let pool = rayon::ThreadPoolBuilder::new()
            .num_threads(4)
            .build()
            .map_err(|e| e.to_string())?;
        let mut sizes = vec![0u64; tree.nodes.len()];
        let mut incomplete: Vec<bool> = tree.nodes.iter().map(|n| n.has_no_permission()).collect();
        pool.install(|| {
            sizes
                .par_iter_mut()
                .zip(incomplete.par_iter_mut())
                .enumerate()
                .for_each(|(id, (size, unknown))| {
                    if self.cancel.load(Ordering::Relaxed) || tree.nodes[id].is_directory() {
                        return;
                    }
                    // Reparse points are opened themselves, never their targets.
                    if let Some(bytes) = allocated(&tree.get_full_path(id as u32)) {
                        *size = bytes;
                    } else {
                        *unknown = true;
                        self.failed.fetch_add(1, Ordering::Relaxed);
                    }
                    self.processed.fetch_add(1, Ordering::Relaxed);
                })
        });
        if self.cancel.load(Ordering::Relaxed) {
            return Ok(());
        }
        // Coordinator inserts parents before children; reverse order aggregates
        // complete subtrees without allocating managed nodes or following links.
        for id in (0..tree.nodes.len()).rev() {
            let parent = tree.nodes[id].parent;
            if parent != NO_INDEX {
                let bytes = sizes[id];
                let unknown = incomplete[id];
                sizes[parent as usize] = sizes[parent as usize].saturating_add(bytes);
                incomplete[parent as usize] = incomplete[parent as usize] || unknown;
            }
        }
        *self.data.lock().unwrap_or_else(|p| p.into_inner()) =
            Some(Arc::new(Data { sizes, incomplete }));
        Ok(())
    }
}
pub fn data(snapshot: &Snapshot) -> Option<Arc<Data>> {
    let job = snapshot
        .allocation
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone()?;
    let data = job.data.lock().unwrap_or_else(|p| p.into_inner()).clone();
    data
}
pub fn cancel(snapshot: &Snapshot) {
    if let Some(job) = snapshot
        .allocation
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .as_ref()
    {
        job.cancel.store(true, Ordering::Relaxed);
    }
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_allocation_start(snapshot: *const Snapshot) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    let mut cache = snapshot
        .allocation
        .lock()
        .unwrap_or_else(|p| p.into_inner());
    if let Some(job) = cache.as_ref() {
        if !job.done.load(Ordering::Acquire) || !job.cancel.load(Ordering::Relaxed) {
            return job.state();
        }
    }
    let job = Arc::new(Job {
        cancel: AtomicBool::new(false),
        done: AtomicBool::new(false),
        processed: AtomicU64::new(0),
        failed: AtomicU64::new(0),
        total: snapshot
            .tree
            .nodes
            .iter()
            .filter(|n| !n.is_directory())
            .count() as u64,
        data: Mutex::new(None),
        error: Mutex::new(None),
    });
    let worker = job.clone();
    let tree = snapshot.tree.clone();
    if let Err(e) = std::thread::Builder::new()
        .name("edirstat-allocation".into())
        .spawn(move || {
            let result =
                std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| worker.run(&tree)));
            let error = match result {
                Ok(Ok(())) => None,
                Ok(Err(e)) => Some(e),
                Err(_) => Some("Allocation worker failed".into()),
            };
            *worker.error.lock().unwrap_or_else(|p| p.into_inner()) = error;
            worker.done.store(true, Ordering::Release);
        })
    {
        return Buffer::error(e);
    }
    *cache = Some(job.clone());
    job.state()
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_allocation_state(snapshot: *const Snapshot) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    let cache = snapshot
        .allocation
        .lock()
        .unwrap_or_else(|p| p.into_inner());
    cache.as_ref().map_or_else(
        || Buffer::error("Allocation has not started"),
        |job| job.state(),
    )
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_allocation_cancel(snapshot: *const Snapshot) {
    if let Some(snapshot) = unsafe { snapshot.as_ref() } {
        cancel(snapshot);
    }
}

#[cfg(target_os = "windows")]
fn allocated(path: &str) -> Option<u64> {
    use std::{ffi::c_void, ptr};
    #[repr(C)]
    struct Standard {
        allocation: i64,
        end: i64,
        links: u32,
        deleting: u8,
        directory: u8,
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateFileW(
            path: *const u16,
            access: u32,
            share: u32,
            security: *const c_void,
            disposition: u32,
            flags: u32,
            template: *mut c_void,
        ) -> *mut c_void;
        fn GetFileInformationByHandleEx(
            file: *mut c_void,
            class: u32,
            info: *mut c_void,
            size: u32,
        ) -> i32;
        fn CloseHandle(file: *mut c_void) -> i32;
    }
    let extended = if path.starts_with("\\\\?\\") {
        path.to_owned()
    } else if let Some(rest) = path.strip_prefix("\\\\") {
        format!("\\\\?\\UNC\\{rest}")
    } else {
        format!("\\\\?\\{path}")
    };
    let wide: Vec<u16> = extended.encode_utf16().chain(Some(0)).collect();
    unsafe {
        let file = CreateFileW(
            wide.as_ptr(),
            0x80,
            7,
            ptr::null(),
            3,
            0x02200000,
            ptr::null_mut(),
        );
        if file as isize == -1 {
            return None;
        }
        let mut info = Standard {
            allocation: 0,
            end: 0,
            links: 0,
            deleting: 0,
            directory: 0,
        };
        let ok = GetFileInformationByHandleEx(
            file,
            1,
            (&mut info as *mut Standard).cast(),
            std::mem::size_of::<Standard>() as u32,
        );
        CloseHandle(file);
        (ok != 0 && info.allocation >= 0).then_some(info.allocation as u64)
    }
}
#[cfg(not(target_os = "windows"))]
fn allocated(_: &str) -> Option<u64> {
    None
}
