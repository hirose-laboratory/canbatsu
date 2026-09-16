using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>検出された1本の木(ワールド座標)</summary>
    public class MsTree
    {
        public float X, Z;            // 幹のワールドXZ位置
        public float GroundY;         // 地面の高さ(ワールドY)
        public int PointCount;        // 検出根拠の点数
        public float YExtent;         // 点の縦の広がり [m]
        public float RadiusXZ;        // 点のXZ広がり半径 [m]
        public float WidthMeters;     // 幹の太さ概算 [m](前面の点のみなので過小気味)
        public bool IsTooClose;       // 他の木と近すぎる
        public bool IsManual;         // 手動タグ付けされた木(うなずき操作)
        public int SeenCount;         // ML検出で確認された回数(MlTreeTrackerが使用。2回以上で表示=幽霊対策)
        public Vector3 TrunkBase => new Vector3(X, GroundY, Z);

        public float HorizontalDistanceFrom(Vector3 p)
            => Mathf.Sqrt((X - p.x) * (X - p.x) + (Z - p.z) * (Z - p.z));
    }

    /// <summary>近すぎる木ペア</summary>
    public struct MsTreePair
    {
        public int IndexA, IndexB;
        public float DistanceMeters;
    }

    /// <summary>
    /// ワールド座標ベースの木検出 (Python/tree_detect_ms.py の忠実な移植)
    ///
    /// モーションステレオの三角測量点群(重力整列済みワールド座標)から:
    ///   1. 地面レベルを高さヒストグラム最頻値で推定
    ///   2. 幹の高さ帯(地上0.5〜3m)の点をXZ平面へ投影
    ///   3. 支配的な直線(建物面・塀)上の点をRANSACで除去
    ///   4. グリッド連結 + 密度ピーク分割 → コンパクトで縦長のクラスタ = 幹
    ///
    /// 純計算のみ(ワーカースレッド実行可)。
    /// </summary>
    public static class TreeDetectorMS
    {
        public class Config
        {
            public float MinTrunkHeight = 0.5f;   // 幹帯の下限(地上からの高さ)
            public float MaxTrunkHeight = 3.0f;   // 幹帯の上限
            public float CellSize = 0.25f;        // XZグリッドセル [m]
            public int MinPoints = 5;             // 幹1本の最小点数
            public float MaxXZRadius = 0.45f;     // 幹クラスタの最大XZ半径 [m]
            public float MinYExtent = 0.6f;       // 幹クラスタの最小縦広がり [m]
            public float PeakSeparation = 0.7f;   // 密度ピーク(幹の種)の最小間隔 [m]
            public float MaxRangeMeters = 10f;    // 基準位置からこの距離を超える木を除外
            public float ClosePairMeters = 1.0f;  // 近すぎ判定の閾値

            // 直線除去(建物面)を発動させる最小点数。40だと2本の幹の中心を通る直線が
            // 両方の幹を「塀」と誤認して消す (2026-09-02の合成検証で確認。README_統合メモの
            // 「癖」の対策案どおり、点群が十分密になるまで発動させない)
            public int LineRemovalMinPoints = 200;
        }

        /// <summary>
        /// 点群から木を検出する。
        /// </summary>
        /// <param name="points">三角測量点群(ワールド座標)</param>
        /// <param name="cameraY">カメラの高さ(ワールドY)。地面推定に使う</param>
        /// <param name="rangeReference">距離フィルタの基準位置(現在のカメラ位置など)</param>
        /// <param name="groundY">推定された地面の高さ</param>
        /// <param name="closePairs">近すぎるペア一覧(IsTooCloseも設定される)</param>
        public static List<MsTree> Detect(IReadOnlyList<Vector3> points, float cameraY,
                                          Vector3 rangeReference,
                                          out float groundY, out List<MsTreePair> closePairs,
                                          Config cfg = null)
        {
            cfg = cfg ?? new Config();
            closePairs = new List<MsTreePair>();
            var trees = new List<MsTree>();
            groundY = cameraY - 1.4f; // フォールバック

            if (points == null || points.Count < cfg.MinPoints)
                return trees;

            // ---- 1. 地面レベル(カメラより0.5m以上低い点の高さヒストグラム最頻値) ----
            var low = new List<float>();
            foreach (var p in points)
                if (p.y < cameraY - 0.5f) low.Add(p.y);
            if (low.Count > 20)
            {
                low.Sort();
                float lo = low[0], hi = low[low.Count - 1];
                int nbins = Mathf.Max(1, (int)((hi - lo) / 0.15f) + 1);
                var hist = new int[nbins];
                foreach (var y in low)
                    hist[Mathf.Min(nbins - 1, (int)((y - lo) / 0.15f))]++;
                int bi = 0;
                for (int i = 1; i < nbins; i++)
                    if (hist[i] > hist[bi]) bi = i;
                groundY = lo + bi * 0.15f + 0.075f;
            }
            else if (points.Count > 0)
            {
                var ys = new List<float>();
                foreach (var p in points) ys.Add(p.y);
                ys.Sort();
                groundY = ys[(int)((ys.Count - 1) * 0.08f)];
            }

            // ---- 1.5. 【斜面対応】2mグリッドの局所地面 ----
            // 平地前提だと斜面の地形点が幹帯に入り幽霊の木になる。セルごとに
            // 「セル最低値+1.5m以内のヒストグラム(0.15m)最頻ビン」を局所地面とし(ノイズ頑健)、
            // 「隣接セルより0.6m以上高くなれない」包絡平滑化で幹の根本の誤認を抑える
            const float GCELL = 2.0f;
            var cellYs = new Dictionary<(int, int), List<float>>();
            foreach (var p in points)
            {
                var key = ((int)Mathf.Floor(p.x / GCELL), (int)Mathf.Floor(p.z / GCELL));
                if (!cellYs.TryGetValue(key, out var lst)) cellYs[key] = lst = new List<float>();
                lst.Add(p.y);
            }
            var cellGround = new Dictionary<(int, int), float>();
            foreach (var kv in cellYs)
            {
                if (kv.Value.Count < 5) continue;
                kv.Value.Sort();
                float lo2 = kv.Value[0];
                int nb2 = (int)(1.5f / 0.15f) + 1;
                var hist2 = new int[nb2];
                foreach (var y in kv.Value)
                {
                    if (y > lo2 + 1.5f) break;
                    hist2[Mathf.Min(nb2 - 1, (int)((y - lo2) / 0.15f))]++;
                }
                int bi2 = 0;
                for (int i = 1; i < nb2; i++)
                    if (hist2[i] > hist2[bi2]) bi2 = i;
                cellGround[kv.Key] = lo2 + bi2 * 0.15f + 0.075f;
            }
            // 幹しか写っていない孤立セルは「幹の最下点」が偽の地面になり、幹帯が上へずれて
            // 縦の広がり条件を満たせなくなる (2026-09-02の合成検証で確認。序盤の疎な点群で顕著)。
            // 隣接セルの支えが無く、全体地面より0.8m以上高い推定は捨てて全体地面に任せる
            {
                var isolatedHigh = new List<(int, int)>();
                foreach (var kv in cellGround)
                {
                    bool hasNeighbor = false;
                    for (int dx = -1; dx <= 1 && !hasNeighbor; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            if (cellGround.ContainsKey((kv.Key.Item1 + dx, kv.Key.Item2 + dz)))
                            { hasNeighbor = true; break; }
                        }
                    if (!hasNeighbor && kv.Value > groundY + 0.8f)
                        isolatedHigh.Add(kv.Key);
                }
                foreach (var key in isolatedHigh) cellGround.Remove(key);
            }

            for (int it = 0; it < 2; it++)
            {
                var updated = new Dictionary<(int, int), float>();
                foreach (var kv in cellGround)
                {
                    float best = kv.Value;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            var nb = (kv.Key.Item1 + dx, kv.Key.Item2 + dz);
                            if (cellGround.TryGetValue(nb, out var g))
                                best = Mathf.Min(best, g + 0.6f);
                        }
                    updated[kv.Key] = best;
                }
                cellGround = updated;
            }
            float globalGround = groundY;
            float LocalGround(float x, float z)
            {
                var key = ((int)Mathf.Floor(x / GCELL), (int)Mathf.Floor(z / GCELL));
                if (cellGround.TryGetValue(key, out var g)) return g;
                float sum = 0; int cnt = 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cellGround.TryGetValue((key.Item1 + dx, key.Item2 + dz), out var gn))
                        { sum += gn; cnt++; }
                return cnt > 0 ? sum / cnt : globalGround;
            }

            // ---- 2. 幹の高さ帯(局所地面基準) ----
            var trunk = new List<Vector3>();
            foreach (var p in points)
            {
                float lg = LocalGround(p.x, p.z);
                if (p.y > lg + cfg.MinTrunkHeight && p.y < lg + cfg.MaxTrunkHeight)
                    trunk.Add(p);
            }
            if (trunk.Count < cfg.MinPoints)
                return trees;

            // ---- 3. 支配的な直線(建物面・塀)の点を除去(RANSAC、最大2本) ----
            var keep = new bool[trunk.Count];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;
            var rng = new System.Random(0);
            for (int round = 0; round < 2; round++)
            {
                var alive = new List<int>();
                for (int i = 0; i < trunk.Count; i++)
                    if (keep[i]) alive.Add(i);
                if (alive.Count < cfg.LineRemovalMinPoints)
                    break;
                List<int> bestInl = null;
                for (int t = 0; t < 200; t++)
                {
                    int i1 = alive[rng.Next(alive.Count)];
                    int i2 = alive[rng.Next(alive.Count)];
                    if (i1 == i2) continue;
                    float dx = trunk[i2].x - trunk[i1].x, dz = trunk[i2].z - trunk[i1].z;
                    float L = Mathf.Sqrt(dx * dx + dz * dz);
                    if (L < 2f) continue;
                    float nx = -dz / L, nz = dx / L;
                    var inl = new List<int>();
                    foreach (var i in alive)
                    {
                        float d = Mathf.Abs((trunk[i].x - trunk[i1].x) * nx + (trunk[i].z - trunk[i1].z) * nz);
                        if (d < 0.25f) inl.Add(i);
                    }
                    if (bestInl == null || inl.Count > bestInl.Count) bestInl = inl;
                }
                if (bestInl != null && bestInl.Count > Mathf.Max(40f, 0.3f * alive.Count))
                    foreach (var i in bestInl) keep[i] = false;
                else
                    break;
            }
            var tp = new List<Vector3>();
            for (int i = 0; i < trunk.Count; i++)
                if (keep[i]) tp.Add(trunk[i]);
            if (tp.Count < cfg.MinPoints)
                return trees;

            // ---- 4. XZグリッド連結成分 ----
            float cell = cfg.CellSize;
            var cellMap = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < tp.Count; i++)
            {
                var key = ((int)Mathf.Floor(tp[i].x / cell), (int)Mathf.Floor(tp[i].z / cell));
                if (!cellMap.TryGetValue(key, out var lst)) cellMap[key] = lst = new List<int>();
                lst.Add(i);
            }
            var visitedCells = new HashSet<(int, int)>();
            var components = new List<List<int>>();
            foreach (var start in cellMap.Keys)
            {
                if (visitedCells.Contains(start)) continue;
                var stack = new Stack<(int, int)>();
                stack.Push(start); visitedCells.Add(start);
                var ids = new List<int>();
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    ids.AddRange(cellMap[c]);
                    for (int da = -1; da <= 1; da++)
                        for (int db = -1; db <= 1; db++)
                        {
                            var nb = (c.Item1 + da, c.Item2 + db);
                            if (cellMap.ContainsKey(nb) && !visitedCells.Contains(nb))
                            {
                                visitedCells.Add(nb);
                                stack.Push(nb);
                            }
                        }
                }
                if (ids.Count >= cfg.MinPoints)
                    components.Add(ids);
            }

            // ---- 5. 密度ピーク分割 → 幹条件フィルタ ----
            foreach (var ids in components)
            {
                var counts = new Dictionary<(int, int), int>();
                foreach (var i in ids)
                {
                    var key = ((int)Mathf.Floor(tp[i].x / cell), (int)Mathf.Floor(tp[i].z / cell));
                    counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                }
                // 局所極大セル(8近傍で最大かつ2点以上)を候補種に
                var peaks = new List<(float x, float z, int n)>();
                foreach (var kv in counts)
                {
                    if (kv.Value < 2) continue;
                    bool isMax = true;
                    for (int da = -1; da <= 1 && isMax; da++)
                        for (int db = -1; db <= 1; db++)
                        {
                            if (da == 0 && db == 0) continue;
                            if (counts.TryGetValue((kv.Key.Item1 + da, kv.Key.Item2 + db), out var nn) && nn > kv.Value)
                            { isMax = false; break; }
                        }
                    if (isMax)
                        peaks.Add(((kv.Key.Item1 + 0.5f) * cell, (kv.Key.Item2 + 0.5f) * cell, kv.Value));
                }
                // 点数の多い順(同数は座標順で決定的に)、既存種からPEAK_SEP以上離れているものだけ採用
                peaks.Sort((p, q) =>
                {
                    int c = q.n.CompareTo(p.n);
                    if (c != 0) return c;
                    c = p.x.CompareTo(q.x);
                    return c != 0 ? c : p.z.CompareTo(q.z);
                });
                var seeds = new List<(float x, float z)>();
                foreach (var (px, pz, n) in peaks)
                {
                    bool ok = true;
                    foreach (var s in seeds)
                        if (Mathf.Sqrt((px - s.x) * (px - s.x) + (pz - s.z) * (pz - s.z)) < cfg.PeakSeparation)
                        { ok = false; break; }
                    if (ok) seeds.Add((px, pz));
                }
                if (seeds.Count == 0) continue;

                var groups = new List<List<int>>();
                for (int s = 0; s < seeds.Count; s++) groups.Add(new List<int>());
                foreach (var i in ids)
                {
                    int best = 0; float bd = float.MaxValue;
                    for (int s = 0; s < seeds.Count; s++)
                    {
                        float d = (tp[i].x - seeds[s].x) * (tp[i].x - seeds[s].x) +
                                  (tp[i].z - seeds[s].z) * (tp[i].z - seeds[s].z);
                        if (d < bd) { bd = d; best = s; }
                    }
                    groups[best].Add(i);
                }
                foreach (var g in groups)
                {
                    if (g.Count < cfg.MinPoints) continue;
                    float mx = 0, mz = 0;
                    foreach (var i in g) { mx += tp[i].x; mz += tp[i].z; }
                    mx /= g.Count; mz /= g.Count;
                    float rMax = 0, yMin = float.MaxValue, yMax = float.MinValue, vx = 0, vz = 0;
                    foreach (var i in g)
                    {
                        float dx = tp[i].x - mx, dz = tp[i].z - mz;
                        rMax = Mathf.Max(rMax, Mathf.Sqrt(dx * dx + dz * dz));
                        yMin = Mathf.Min(yMin, tp[i].y);
                        yMax = Mathf.Max(yMax, tp[i].y);
                        vx += dx * dx; vz += dz * dz;
                    }
                    float yExt = yMax - yMin;
                    if (rMax > cfg.MaxXZRadius || yExt < cfg.MinYExtent) continue;
                    float width = 2f * Mathf.Sqrt(vx / g.Count + vz / g.Count);
                    trees.Add(new MsTree
                    {
                        X = mx, Z = mz, GroundY = LocalGround(mx, mz),
                        PointCount = g.Count, YExtent = yExt,
                        RadiusXZ = rMax, WidthMeters = width,
                    });
                }
            }

            // ---- 6. 距離フィルタ + 近接ペア ----
            trees.RemoveAll(t => t.HorizontalDistanceFrom(rangeReference) > cfg.MaxRangeMeters);
            trees.Sort((a, b) => a.X.CompareTo(b.X));
            for (int i = 0; i < trees.Count; i++)
            {
                for (int j = i + 1; j < trees.Count; j++)
                {
                    float dx = trees[i].X - trees[j].X, dz = trees[i].Z - trees[j].Z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < cfg.ClosePairMeters)
                    {
                        closePairs.Add(new MsTreePair { IndexA = i, IndexB = j, DistanceMeters = d });
                        trees[i].IsTooClose = true;
                        trees[j].IsTooClose = true;
                    }
                }
            }
            return trees;
        }
    }
}
