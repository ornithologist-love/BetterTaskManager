use crate::{
    Buffer, Snapshot,
    arena::{FileArenaSnapshot, NO_INDEX},
};
use serde::Serialize;
use serde_json::{Value, json};
use std::{
    cmp::{Ordering, Reverse},
    collections::{BinaryHeap, HashMap},
    sync::Arc,
};

fn name(tree: &FileArenaSnapshot, id: u32) -> &str {
    tree.string_pool
        .get(tree.nodes[id as usize].name_id)
        .unwrap_or("")
}
fn extension(name: &str) -> String {
    crate::arena::get_ext_slice(name).to_lowercase()
}
fn color(extension: &str) -> u32 {
    extension.bytes().fold(2166136261u32, |h, b| {
        (h ^ u32::from(b)).wrapping_mul(16777619)
    })
}
pub fn entry(snapshot: &Snapshot, id: u32) -> Value {
    let tree = &snapshot.tree;
    let allocated = crate::allocation::data(snapshot);
    let node = &tree.nodes[id as usize];
    let parent_size = tree
        .nodes
        .get(node.parent as usize)
        .map_or(node.size, |p| p.size);
    json!({"id": id, "parent": node.parent, "name": name(tree,id), "size": node.size,
        "directory": node.is_directory(), "denied": node.has_no_permission(),
        "files": if node.is_directory() {node.file_count} else {1},
        "directories": tree.dir_counts.get(id as usize).copied().unwrap_or(0),
        "modified": node.modified_timestamp, "parentSize": parent_size,
        "allocatedSize": allocated.as_ref().map(|a|a.sizes[id as usize]),
        "allocationIncomplete": allocated.as_ref().is_some_and(|a|a.incomplete[id as usize]),
        "extension": if node.is_directory() {String::new()} else {extension(name(tree,id))}})
}
pub fn compare(tree: &FileArenaSnapshot, a: u32, b: u32, sort: u32, descending: bool) -> Ordering {
    let (x, y) = (&tree.nodes[a as usize], &tree.nodes[b as usize]);
    let order = match sort {
        0 => name(tree, a).cmp(name(tree, b)),
        2 => x.file_count.cmp(&y.file_count),
        3 => tree.dir_counts[a as usize].cmp(&tree.dir_counts[b as usize]),
        4 => x.modified_timestamp.cmp(&y.modified_timestamp),
        _ => x.size.cmp(&y.size),
    };
    (if descending { order.reverse() } else { order }).then(a.cmp(&b))
}

/// Depth-first walk keeps pending indices in Rust, never a managed node list.
struct Walk<'a> {
    tree: &'a FileArenaSnapshot,
    stack: Vec<u32>,
}
impl<'a> Walk<'a> {
    fn new(tree: &'a FileArenaSnapshot, root: u32) -> Self {
        Self {
            tree,
            stack: vec![root],
        }
    }
}
impl Iterator for Walk<'_> {
    type Item = u32;
    fn next(&mut self) -> Option<u32> {
        let id = self.stack.pop()?;
        let mut child = self.tree.nodes[id as usize].first_child;
        while child != NO_INDEX {
            self.stack.push(child);
            child = self.tree.nodes[child as usize].next_sibling;
        }
        Some(id)
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_node(
    snapshot: *const Snapshot,
    id: u32,
    sort: u32,
    descending: u32,
) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    let tree = &snapshot.tree;
    let Some(node) = tree.nodes.get(id as usize) else {
        return Buffer::error("Unknown node");
    };
    let mut ancestors = Vec::new();
    let mut current = node.parent;
    while current != NO_INDEX {
        ancestors.push(current);
        current = tree.nodes[current as usize].parent;
    }
    ancestors.reverse();
    let mut offset = 0;
    if node.parent != NO_INDEX && sort != NO_INDEX {
        let mut sibling = tree.nodes[node.parent as usize].first_child;
        while sibling != NO_INDEX {
            if compare(tree, sibling, id, sort, descending != 0).is_lt() {
                offset += 1;
            }
            sibling = tree.nodes[sibling as usize].next_sibling;
        }
    }
    Buffer::json(
        json!({"error":null,"entry":entry(snapshot,id),"path":tree.get_full_path(id),"ancestors":ancestors,"offset":offset}),
    )
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Extension {
    extension: String,
    size: u64,
    files: u64,
    color: u32,
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_extensions(
    snapshot: *const Snapshot,
    root: u32,
) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    if snapshot.tree.nodes.get(root as usize).is_none() {
        return Buffer::error("Unknown node");
    }
    let mut items: Vec<Extension> = if root == 0 {
        snapshot
            .extensions
            .iter()
            .map(|(ext, size, files)| Extension {
                extension: ext.to_string(),
                size: *size,
                files: u64::from(*files),
                color: color(ext),
            })
            .collect()
    } else {
        let mut totals: HashMap<String, (u64, u64)> = HashMap::new();
        for id in Walk::new(&snapshot.tree, root) {
            let node = &snapshot.tree.nodes[id as usize];
            if node.is_directory() {
                continue;
            }
            let total = totals
                .entry(extension(name(&snapshot.tree, id)))
                .or_default();
            total.0 = total.0.saturating_add(node.size);
            total.1 += 1;
        }
        totals
            .into_iter()
            .map(|(ext, (size, files))| Extension {
                color: color(&ext),
                extension: ext,
                size,
                files,
            })
            .collect()
    };
    items.sort_unstable_by(|a, b| b.size.cmp(&a.size).then(a.extension.cmp(&b.extension)));
    Buffer::serialized(&items)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_files(
    snapshot: *const Snapshot,
    root: u32,
    filter: *const u8,
    len: usize,
) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    if snapshot.tree.nodes.get(root as usize).is_none() {
        return Buffer::error("Unknown node");
    }
    if filter.is_null() || len > 4096 {
        return Buffer::error("Invalid filter");
    }
    let query: Value =
        match serde_json::from_slice(unsafe { std::slice::from_raw_parts(filter, len) }) {
            Ok(v) => v,
            Err(e) => return Buffer::error(e),
        };
    let ext = query["extension"].as_str();
    let text = query["name"].as_str().unwrap_or("").to_lowercase();
    let mut top: BinaryHeap<Reverse<(u64, u32)>> = BinaryHeap::new();
    let mut matched = 0u64;
    for id in Walk::new(&snapshot.tree, root) {
        let node = &snapshot.tree.nodes[id as usize];
        if node.is_directory() {
            continue;
        }
        let name = name(&snapshot.tree, id);
        if ext.is_some_and(|ext| extension(name) != ext)
            || (!text.is_empty() && !crate::arena::contains_case_insensitive(name, &text))
        {
            continue;
        }
        matched += 1;
        let item = (node.size, id);
        if top.len() < 1000 {
            top.push(Reverse(item));
        } else if top.peek().is_some_and(|head| item > head.0) {
            top.pop();
            top.push(Reverse(item));
        }
    }
    let mut top: Vec<_> = top.into_iter().map(|x| x.0).collect();
    top.sort_unstable_by(|a, b| b.cmp(a));
    let items: Vec<_> = top
        .into_iter()
        .map(|(_, id)| {
            let mut value = entry(snapshot, id);
            value["path"] = json!(snapshot.tree.get_full_path(id));
            value
        })
        .collect();
    Buffer::json(json!({"error":null,"matched":matched,"items":items}))
}

#[derive(Clone, Copy)]
struct Rect {
    x: f64,
    y: f64,
    w: f64,
    h: f64,
}
impl Rect {
    fn area(self) -> f64 {
        self.w * self.h
    }
}
#[derive(Serialize)]
struct Block {
    id: u32,
    parent: u32,
    depth: u32,
    directory: bool,
    free: bool,
    header: f64,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
    size: u64,
    color: u32,
    label: String,
}
struct Layout<'a> {
    tree: &'a FileArenaSnapshot,
    blocks: Vec<Block>,
    pending: Vec<(u32, u32, Rect, u32)>,
    quantum: f64,
    label_height: f64,
    allocated: Option<Arc<crate::allocation::Data>>,
}
impl Layout<'_> {
    fn size(&self, id: u32) -> u64 {
        self.allocated
            .as_ref()
            .map_or(self.tree.nodes[id as usize].size, |a| a.sizes[id as usize])
    }
    // Sample uniformly over the viewport. Every branch receives the same pixel
    // resolution; an earlier branch cannot consume a later branch's detail.
    fn sampled(&self, rect: Rect) -> Rect {
        let q = self.quantum;
        let left = (rect.x / q - 0.5).ceil() * q;
        let top = (rect.y / q - 0.5).ceil() * q;
        let right = ((rect.x + rect.w) / q - 0.5).ceil() * q;
        let bottom = ((rect.y + rect.h) / q - 0.5).ceil() * q;
        Rect {
            x: left,
            y: top,
            w: right - left,
            h: bottom - top,
        }
    }
    fn block(&mut self, id: u32, parent: u32, rect: Rect, depth: u32, header: f64) {
        let n = &self.tree.nodes[id as usize];
        let directory = n.is_directory();
        let label = if (directory && header > 0.0) || (!directory && rect.w > 90.0 && rect.h > 22.0)
        {
            name(self.tree, id).into()
        } else {
            String::new()
        };
        self.blocks.push(Block {
            id,
            parent,
            depth,
            directory,
            free: false,
            header,
            x: rect.x,
            y: rect.y,
            w: rect.w,
            h: rect.h,
            size: self.size(id),
            color: if directory {
                0
            } else {
                color(&extension(name(self.tree, id)))
            },
            label,
        });
    }
    fn visit(&mut self, id: u32, parent: u32, rect: Rect, depth: u32) {
        let n = &self.tree.nodes[id as usize];
        let sampled = self.sampled(rect);
        let size = self.size(id);
        if size == 0 || sampled.w <= 0.0 || sampled.h <= 0.0 {
            return;
        }
        if !n.is_directory() {
            self.block(id, parent, sampled, depth, 0.0);
            return;
        }
        let header =
            if depth > 0 && rect.w > 6.0 * self.label_height && rect.h > 3.0 * self.label_height {
                self.label_height
            } else {
                0.0
            };
        let inset = if rect.w > 12.0 && rect.h > 12.0 {
            1.0
        } else {
            0.0
        };
        // A directory that has no visible frame needs no bitmap/JSON block.
        // Its children still receive the full area, including single-pixel files.
        if header > 0.0 || inset > 0.0 || depth == 0 {
            self.block(id, parent, sampled, depth, header);
        }
        let inner = Rect {
            x: rect.x + inset,
            y: rect.y + inset + header,
            w: (rect.w - 2.0 * inset).max(0.0),
            h: (rect.h - 2.0 * inset - header).max(0.0),
        };
        if inner.area() <= 0.0 {
            return;
        }
        let scale = inner.area() / size as f64;
        let mut children = Vec::new();
        let mut child = n.first_child;
        while child != NO_INDEX {
            let node = &self.tree.nodes[child as usize];
            let size = self.size(child);
            if size > 0 {
                children.push((child, size));
            }
            child = node.next_sibling;
        }
        children.sort_unstable_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(&b.0)));
        let pending_start = self.pending.len();
        let mut remaining = inner;
        let mut first = 0;
        while first < children.len() {
            let short = remaining.w.min(remaining.h);
            if short <= 0.0 {
                break;
            }
            let mut end = first + 1;
            let mut sum = children[first].1 as f64 * scale;
            let max = sum;
            let mut min = sum;
            let mut worst = aspect(sum, min, max, short);
            while end < children.len() {
                let area = children[end].1 as f64 * scale;
                let candidate = aspect(sum + area, area.min(min), max, short);
                if candidate > worst {
                    break;
                }
                sum += area;
                min = min.min(area);
                worst = candidate;
                end += 1;
            }
            let thickness = sum / short;
            let mut position = if remaining.w >= remaining.h {
                remaining.y
            } else {
                remaining.x
            };
            for &(child, size) in &children[first..end] {
                let length = size as f64 * scale / thickness;
                let tile = if remaining.w >= remaining.h {
                    Rect {
                        x: remaining.x,
                        y: position,
                        w: thickness.min(remaining.w),
                        h: length,
                    }
                } else {
                    Rect {
                        x: position,
                        y: remaining.y,
                        w: length,
                        h: thickness.min(remaining.h),
                    }
                };
                let sampled = self.sampled(tile);
                if sampled.w > 0.0 && sampled.h > 0.0 {
                    self.pending.push((child, id, tile, depth + 1));
                }
                position += length;
            }
            if remaining.w >= remaining.h {
                remaining.x += thickness;
                remaining.w = (remaining.w - thickness).max(0.0);
            } else {
                remaining.y += thickness;
                remaining.h = (remaining.h - thickness).max(0.0);
            }
            first = end;
        }
        // Iterative depth-first traversal avoids a fixed hierarchy cutoff and
        // keeps parent frames before their child pixels without recursive stacks.
        self.pending[pending_start..].reverse();
    }
}
fn aspect(sum: f64, min: f64, max: f64, side: f64) -> f64 {
    if min <= 0.0 || sum <= 0.0 {
        return f64::INFINITY;
    }
    let square = side * side;
    (square * max / (sum * sum)).max(sum * sum / (square * min))
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn edirstat_snapshot_map(
    snapshot: *const Snapshot,
    root: u32,
    width: f64,
    height: f64,
    render_scale: f64,
    allocated: u32,
    volume_used: u64,
    volume_free: u64,
) -> Buffer {
    let Some(snapshot) = (unsafe { snapshot.as_ref() }) else {
        return Buffer::error("Invalid snapshot");
    };
    if snapshot.tree.nodes.get(root as usize).is_none() {
        return Buffer::error("Unknown node");
    }
    if !width.is_finite()
        || !height.is_finite()
        || width < 1.0
        || height < 1.0
        || width > 10000.0
        || height > 10000.0
        || !render_scale.is_finite()
        || !(0.5..=8.0).contains(&render_scale)
    {
        return Buffer::error("Invalid viewport");
    }
    let allocations = if allocated & 1 != 0 {
        let Some(data) = crate::allocation::data(snapshot) else {
            return Buffer::error("Allocation data is not ready");
        };
        Some(data)
    } else {
        None
    };
    let map_width = if volume_free > 0 {
        width * volume_used as f64 / (volume_used as f64 + volume_free as f64)
    } else {
        width
    };
    let mut layout = Layout {
        tree: &snapshot.tree,
        blocks: Vec::new(),
        pending: vec![(
            root,
            NO_INDEX,
            Rect {
                x: 0.0,
                y: 0.0,
                w: map_width,
                h: height,
            },
            0,
        )],
        // Bound exported detail by visible samples rather than scan order.
        quantum: (width * height / 250000.0).sqrt().ceil().max(1.0),
        label_height: if allocated & 2 != 0 {
            17.0 * render_scale
        } else {
            0.0
        },
        allocated: allocations,
    };
    while let Some((id, parent, rect, depth)) = layout.pending.pop() {
        layout.visit(id, parent, rect, depth);
    }
    if volume_free > 0 {
        layout.blocks.push(Block {
            id: NO_INDEX,
            parent: NO_INDEX,
            depth: 0,
            directory: false,
            free: true,
            header: 0.0,
            x: map_width,
            y: 0.0,
            w: width - map_width,
            h: height,
            size: volume_free,
            color: 0,
            label: "可用空间".into(),
        });
    }
    Buffer::serialized(&layout.blocks)
}
