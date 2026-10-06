//! Decoration hitbox spatial index (uniform grid, SoA AABBs).
//!
//! Rebuild once per frame from collider world AABBs, then answer overlap
//! queries. The host uses it as a conservative broad phase over decorations:
//! a query that returns no candidate proves no physical overlap is possible,
//! so the expensive per-decoration physics query can be skipped. Candidate
//! list is exact for AABB overlap; the host still runs its exact narrow phase
//! for any candidate (tag match / physics contact).

/// One AABB per item, built from packed `[min_x, min_y, max_x, max_y]`.
/// Items covering more than `MAX_CELLS_PER_RECT` cells go to an overflow
/// list checked by every query (avoids pathological insertions).
const MAX_CELLS_PER_RECT: u32 = 32;
const MAX_DIM: u32 = 512;

#[derive(Default)]
pub struct Grid {
    rects: Vec<f32>, // 4 per item
    count: usize,
    min_x: f32,
    min_y: f32,
    cell_x: f32,
    cell_y: f32,
    cols: u32,
    rows: u32,
    cell_start: Vec<u32>,
    cell_items: Vec<u32>,
    overflow: Vec<u32>,
    cursor: Vec<u32>,
    stamp: Vec<u32>,
    gen: u32,
    scratch: Vec<u32>,
}

impl Grid {
    /// Number of indexed items.
    pub fn len(&self) -> usize {
        self.count
    }

    /// Builds the index from packed rects. Zero items clears the index.
    pub fn rebuild(&mut self, packed: &[f32]) {
        self.rects.clear();
        self.rects.extend_from_slice(packed);
        self.count = self.rects.len() / 4;
        self.overflow.clear();
        self.cell_start.clear();
        self.cell_items.clear();
        self.cursor.clear();
        self.cols = 0;
        self.rows = 0;

        if self.count == 0 {
            return;
        }

        let mut min_x = f32::INFINITY;
        let mut min_y = f32::INFINITY;
        let mut max_x = f32::NEG_INFINITY;
        let mut max_y = f32::NEG_INFINITY;
        let mut valid = 0usize;
        for i in 0..self.count {
            let r = &self.rects[i * 4..i * 4 + 4];
            if !(r[0].is_finite() && r[1].is_finite() && r[2].is_finite() && r[3].is_finite()) {
                continue;
            }
            valid += 1;
            min_x = min_x.min(r[0]);
            min_y = min_y.min(r[1]);
            max_x = max_x.max(r[2]);
            max_y = max_y.max(r[3]);
        }
        if valid == 0 {
            return;
        }

        let span_x = (max_x - min_x).max(1e-6);
        let span_y = (max_y - min_y).max(1e-6);
        // Aim for ~4 items per cell.
        let target_cells = (valid as f32 / 4.0).max(1.0);
        let cell = ((span_x * span_y / target_cells).sqrt()).max(1e-4);
        let cols = ((span_x / cell).ceil() as i64).clamp(1, MAX_DIM as i64) as u32;
        let rows = ((span_y / cell).ceil() as i64).clamp(1, MAX_DIM as i64) as u32;

        self.min_x = min_x;
        self.min_y = min_y;
        self.cols = cols;
        self.rows = rows;
        self.cell_x = span_x / cols as f32;
        self.cell_y = span_y / rows as f32;

        let ncells = cols as usize * rows as usize;
        self.cell_start.resize(ncells + 1, 0);

        // Count pass.
        for i in 0..self.count {
            let r = self.rect_at(i);
            if !r[0].is_finite() {
                continue;
            }
            let (cx0, cy0, cx1, cy1) = self.clamp_cells(r);
            let covered =
                (cx1 - cx0 + 1) as u64 * (cy1 - cy0 + 1) as u64;
            if covered > MAX_CELLS_PER_RECT as u64 {
                self.overflow.push(i as u32);
                continue;
            }
            for cy in cy0..=cy1 {
                let row = cy as usize * cols as usize;
                for cx in cx0..=cx1 {
                    self.cell_start[row + cx as usize + 1] += 1;
                }
            }
        }
        for i in 0..ncells {
            self.cell_start[i + 1] += self.cell_start[i];
        }

        // Fill pass (cell_start doubles as write cursor during fill).
        self.cursor.clear();
        self.cursor.extend_from_slice(&self.cell_start[..ncells]);
        let total = self.cell_start[ncells] as usize;
        self.cell_items.clear();
        self.cell_items.resize(total, 0);
        for i in 0..self.count {
            let r = self.rect_at(i);
            if !r[0].is_finite() {
                continue;
            }
            let (cx0, cy0, cx1, cy1) = self.clamp_cells(r);
            let covered =
                (cx1 - cx0 + 1) as u64 * (cy1 - cy0 + 1) as u64;
            if covered > MAX_CELLS_PER_RECT as u64 {
                continue;
            }
            for cy in cy0..=cy1 {
                let row = cy as usize * cols as usize;
                for cx in cx0..=cx1 {
                    let c = row + cx as usize;
                    let pos = self.cursor[c] as usize;
                    self.cell_items[pos] = i as u32;
                    self.cursor[c] += 1;
                }
            }
        }

        self.stamp.clear();
        self.stamp.resize(self.count, 0);
        self.gen = 0;
    }

    /// Returns the indices of all items whose AABB intersects `rect`
    /// (inclusive bounds; conservative, never misses a real overlap).
    pub fn query(&mut self, rect: [f32; 4]) -> &[u32] {
        self.scratch.clear();
        if self.count == 0 || !(rect[0].is_finite() && rect[1].is_finite() && rect[2].is_finite() && rect[3].is_finite()) {
            return &self.scratch;
        }
        self.gen = self.gen.wrapping_add(1);
        if self.gen == 0 {
            for s in &mut self.stamp {
                *s = 0;
            }
            self.gen = 1;
        }
        let gen = self.gen;
        let cols = self.cols as usize;

        if cols != 0 {
            let (cx0, cy0, cx1, cy1) = self.clamp_cells(rect);
            for cy in cy0..=cy1 {
                let row = cy as usize * cols;
                for cx in cx0..=cx1 {
                    let c = row + cx as usize;
                    let s = self.cell_start[c] as usize;
                    let e = self.cell_start[c + 1] as usize;
                    for k in s..e {
                        let id = self.cell_items[k] as usize;
                        if self.stamp[id] == gen {
                            continue;
                        }
                        self.stamp[id] = gen;
                        if intersects(self.rect_at(id), rect) {
                            self.scratch.push(id as u32);
                        }
                    }
                }
            }
        }
        for k in 0..self.overflow.len() {
            let id = self.overflow[k] as usize;
            if self.stamp[id] == gen {
                continue;
            }
            self.stamp[id] = gen;
            if intersects(self.rect_at(id), rect) {
                self.scratch.push(id as u32);
            }
        }
        &self.scratch
    }

    #[inline]
    fn rect_at(&self, i: usize) -> [f32; 4] {
        let r = &self.rects[i * 4..i * 4 + 4];
        [r[0], r[1], r[2], r[3]]
    }

    #[inline]
    fn clamp_cells(&self, r: [f32; 4]) -> (u32, u32, u32, u32) {
        let cx0 = (((r[0] - self.min_x) / self.cell_x).floor() as i64)
            .clamp(0, self.cols as i64 - 1) as u32;
        let cy0 = (((r[1] - self.min_y) / self.cell_y).floor() as i64)
            .clamp(0, self.rows as i64 - 1) as u32;
        let cx1 = (((r[2] - self.min_x) / self.cell_x).floor() as i64)
            .clamp(0, self.cols as i64 - 1) as u32;
        let cy1 = (((r[3] - self.min_y) / self.cell_y).floor() as i64)
            .clamp(0, self.rows as i64 - 1) as u32;
        (cx0, cy0, cx1, cy1)
    }
}

#[inline]
fn intersects(a: [f32; 4], b: [f32; 4]) -> bool {
    a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pack(rects: &[[f32; 4]]) -> Vec<f32> {
        rects.iter().flatten().copied().collect()
    }

    #[test]
    fn basic_query() {
        let mut g = Grid::default();
        g.rebuild(&pack(&[
            [0.0, 0.0, 1.0, 1.0],
            [5.0, 5.0, 6.0, 6.0],
            [0.5, 0.5, 1.5, 1.5],
        ]));
        let mut got = g.query([0.9, 0.9, 1.1, 1.1]).to_vec();
        got.sort();
        assert_eq!(got, vec![0, 2]);
        assert!(g.query([100.0, 100.0, 101.0, 101.0]).is_empty());
        // touching bounds count as overlap (conservative)
        let got = g.query([1.0, 1.0, 1.0, 1.0]).to_vec();
        assert_eq!(got.len(), 2);
    }

    #[test]
    fn zero_items_and_odd_rects() {
        let mut g = Grid::default();
        g.rebuild(&[]);
        assert!(g.query([0.0, 0.0, 1.0, 1.0]).is_empty());
        g.rebuild(&pack(&[[f32::NAN, 0.0, 1.0, 1.0], [0.0, 0.0, 1.0, 1.0]]));
        let got = g.query([0.0, 0.0, 1.0, 1.0]).to_vec();
        assert_eq!(got, vec![1]);
    }

    #[test]
    fn overflow_items_still_found() {
        let mut g = Grid::default();
        g.rebuild(&pack(&[
            [-1000.0, -1000.0, 1000.0, 1000.0], // huge: overflow list
            [0.0, 0.0, 1.0, 1.0],
        ]));
        let got = g.query([0.0, 0.0, 1.0, 1.0]).to_vec();
        assert_eq!(got.len(), 2);
    }

    #[test]
    fn many_items_never_miss() {
        let mut rects = Vec::new();
        for i in 0..1000 {
            let x = (i % 40) as f32 * 2.0;
            let y = (i / 40) as f32 * 2.0;
            rects.push([x, y, x + 1.5, y + 1.5]);
        }
        let mut g = Grid::default();
        g.rebuild(&pack(&rects));
        // Brute force check a few query rects.
        for q in 0..50 {
            let x = (q % 10) as f32 * 3.0 - 1.0;
            let y = (q / 10) as f32 * 4.0 - 1.0;
            let query = [x, y, x + 5.0, y + 5.0];
            let mut got = g.query(query).to_vec();
            got.sort();
            let mut want: Vec<u32> = (0..rects.len() as u32)
                .filter(|&i| {
                    let r = rects[i as usize];
                    r[0] <= query[2] && r[2] >= query[0] && r[1] <= query[3] && r[3] >= query[1]
                })
                .collect();
            want.sort();
            assert_eq!(got, want, "query {q}");
        }
    }
}
