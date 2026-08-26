using System;
using System.Collections.Generic;
using System.Linq;

namespace CanbatsuMS
{
    /// <summary>3D点 (ワールド座標、重力整列済み)。Unity非依存の倍精度 (Python版との数値照合のため)</summary>
    public struct MsPoint3
    {
        public double X, Y, Z;
        public MsPoint3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>検出された幹クラスタ1本分</summary>
    public class MsTree
    {
        public double X, Z;          // ワールドXZ中心
        public int PointCount;
        public double YExtent;       // 縦方向の広がり [m]
        public double RadiusXz;      // XZ平面での最大半径 [m]
        public double Width;         // 太さ概算 (2σ) [m]
        public List<int> PointIds = new List<int>(); // 入力点列に対するインデックス
    }

    public class MsTreeDetectConfig
    {
        public double MinH = 0.5;         // 幹の高さ帯: 地上この高さから
        public double MaxH = 3.0;         // 同・この高さまで
        public double Cell = 0.25;        // XZグリッドのセルサイズ [m]
        public int MinPts = 5;            // クラスタの最小点数
        public double MaxXzRadius = 0.45; // 幹とみなす最大XZ半径 (コンパクト条件)
        public double MinYExtent = 0.6;   // 幹とみなす最小の縦の広がり (縦長条件)
        public double PeakSep = 0.7;      // 密度ピーク (幹の種) 同士の最小距離 [m]
        public int RansacSeed = 0;        // 建物面除去RANSACの乱数シード
    }

    /// <summary>
    /// ワールド座標ベースの木検出 (AI担当の tree_detect_ms.py のC#移植)。
    /// モーションステレオの三角測量点群から:
    ///   1. 地面レベルを高さヒストグラムで推定
    ///   2. 幹の高さ帯 (地上0.5〜3m) の点をXZ平面に投影
    ///   3. 建物面 (支配的な直線) 上の点をRANSACで除去
    ///   4. グリッド連結+密度ピーク分割 → コンパクトで縦長のクラスタ = 幹
    /// Python版との数値照合テストあり (Tests/)。乱数を使うRANSACのみ実装依存 (結果は同等)。
    /// </summary>
    public static class TreeDetectMS
    {
        public static List<MsTree> DetectTrees(
            IReadOnlyList<MsPoint3> points, double camY, out double groundY,
            MsTreeDetectConfig config = null)
        {
            var cfg = config ?? new MsTreeDetectConfig();
            var trees = new List<MsTree>();
            groundY = EstimateGround(points, camY);
            if (points.Count == 0) return trees;

            // 幹の高さ帯の点だけを対象にする (元インデックスを保持)
            var trunkPts = new List<MsPoint3>();
            var idxMap = new List<int>();
            for (int i = 0; i < points.Count; i++)
            {
                double h = points[i].Y - groundY;
                if (h > cfg.MinH && h < cfg.MaxH)
                {
                    trunkPts.Add(points[i]);
                    idxMap.Add(i);
                }
            }
            if (trunkPts.Count < cfg.MinPts) return trees;

            // 建物・塀対策: XZ平面上の支配的な直線に乗る点を除去 (幹は面から手前に立つので消えない)
            RemoveDominantLines(trunkPts, idxMap, cfg);
            if (trunkPts.Count < cfg.MinPts) return trees;

            // XZグリッドで連結クラスタリング (8近傍) → 密度ピーク分割 → 幹条件で評価
            var components = ConnectedComponents(trunkPts, cfg);
            foreach (var ids in components)
            {
                SplitByDensityPeaks(trunkPts, idxMap, ids, cfg, trees);
            }

            trees.Sort((a, b) => a.X.CompareTo(b.X));
            return trees;
        }

        /// <summary>地面レベル: カメラより0.5m以上低い点の高さヒストグラム最頻値 (草に引きずられない)</summary>
        static double EstimateGround(IReadOnlyList<MsPoint3> points, double camY)
        {
            var low = new List<double>();
            var ys = new List<double>();
            foreach (var p in points)
            {
                ys.Add(p.Y);
                if (p.Y < camY - 0.5) low.Add(p.Y);
            }
            if (ys.Count == 0) return 0;

            if (low.Count > 20)
            {
                double min = low.Min(), max = low.Max();
                // np.arange(min, max+0.15, 0.15) と同じ端の作り方
                var edges = new List<double>();
                for (double v = min; v < max + 0.15 - 1e-12; v += 0.15) edges.Add(v);
                if (edges.Count < 2) edges.Add(min + 0.15);
                int bins = edges.Count - 1;
                var hist = new int[bins];
                foreach (var v in low)
                {
                    for (int b = 0; b < bins; b++)
                    {
                        bool last = b == bins - 1;
                        if (v >= edges[b] && (v < edges[b + 1] || (last && v <= edges[b + 1])))
                        {
                            hist[b]++;
                            break;
                        }
                    }
                }
                int arg = 0;
                for (int b = 1; b < bins; b++) if (hist[b] > hist[arg]) arg = b; // 最頻の最初の山
                return edges[arg] + 0.075;
            }

            // 点が少ないときは8パーセンタイル (np.percentileの線形補間と同じ)
            ys.Sort();
            double rank = (ys.Count - 1) * 0.08;
            int lo = (int)Math.Floor(rank);
            double frac = rank - lo;
            return lo + 1 < ys.Count ? ys[lo] + (ys[lo + 1] - ys[lo]) * frac : ys[lo];
        }

        /// <summary>建物面の除去: XZ上の支配的な直線 (最大2本) のインライアを消す (RANSAC 200回)</summary>
        static void RemoveDominantLines(List<MsPoint3> trunkPts, List<int> idxMap, MsTreeDetectConfig cfg)
        {
            var rng = new Random(cfg.RansacSeed);
            var keep = Enumerable.Repeat(true, trunkPts.Count).ToArray();

            for (int round = 0; round < 2; round++)
            {
                var alive = new List<int>();
                for (int i = 0; i < keep.Length; i++) if (keep[i]) alive.Add(i);
                if (alive.Count < 40) break;

                List<int> bestInliers = null;
                for (int t = 0; t < 200; t++)
                {
                    int i1 = alive[rng.Next(alive.Count)];
                    int i2 = alive[rng.Next(alive.Count)];
                    if (i1 == i2) continue;
                    double dx = trunkPts[i2].X - trunkPts[i1].X;
                    double dz = trunkPts[i2].Z - trunkPts[i1].Z;
                    double len = Math.Sqrt(dx * dx + dz * dz);
                    if (len < 2.0) continue; // 短い基線の直線仮説はノイズに弱い
                    double nx = -dz / len, nz = dx / len; // 法線
                    var inliers = new List<int>();
                    foreach (int a in alive)
                    {
                        double d = Math.Abs((trunkPts[a].X - trunkPts[i1].X) * nx +
                                            (trunkPts[a].Z - trunkPts[i1].Z) * nz);
                        if (d < 0.25) inliers.Add(a);
                    }
                    if (bestInliers == null || inliers.Count > bestInliers.Count) bestInliers = inliers;
                }

                // 幹帯の3割以上が1直線に乗るなら建物面とみなして除去
                if (bestInliers != null && bestInliers.Count > Math.Max(40.0, 0.3 * alive.Count))
                {
                    foreach (int i in bestInliers) keep[i] = false;
                }
                else break;
            }

            for (int i = keep.Length - 1; i >= 0; i--)
            {
                if (!keep[i])
                {
                    trunkPts.RemoveAt(i);
                    idxMap.RemoveAt(i);
                }
            }
        }

        /// <summary>XZグリッド (8近傍) の連結成分。戻り値は trunkPts のインデックス群</summary>
        static List<List<int>> ConnectedComponents(List<MsPoint3> trunkPts, MsTreeDetectConfig cfg)
        {
            var cellMap = new Dictionary<(long, long), List<int>>();
            var cellOrder = new List<(long, long)>(); // 挿入順 (Python dictの走査順の再現)
            for (int i = 0; i < trunkPts.Count; i++)
            {
                var key = (Fl(trunkPts[i].X / cfg.Cell), Fl(trunkPts[i].Z / cfg.Cell));
                if (!cellMap.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    cellMap[key] = list;
                    cellOrder.Add(key);
                }
                list.Add(i);
            }

            var visited = new HashSet<(long, long)>();
            var components = new List<List<int>>();
            foreach (var start in cellOrder)
            {
                if (visited.Contains(start)) continue;
                var stack = new Stack<(long, long)>();
                stack.Push(start);
                visited.Add(start);
                var ids = new List<int>();
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    ids.AddRange(cellMap[c]);
                    for (long da = -1; da <= 1; da++)
                    {
                        for (long db = -1; db <= 1; db++)
                        {
                            var nb = (c.Item1 + da, c.Item2 + db);
                            if (cellMap.ContainsKey(nb) && !visited.Contains(nb))
                            {
                                visited.Add(nb);
                                stack.Push(nb);
                            }
                        }
                    }
                }
                if (ids.Count >= cfg.MinPts) components.Add(ids);
            }
            return components;
        }

        /// <summary>
        /// 密度ピーク分割: 成分内のセル点数の局所極大を「幹の種」とし、各点を最寄り種に割り当てて
        /// サブクラスタごとに幹条件 (コンパクト・縦長) を評価する
        /// </summary>
        static void SplitByDensityPeaks(List<MsPoint3> trunkPts, List<int> idxMap,
            List<int> ids, MsTreeDetectConfig cfg, List<MsTree> outTrees)
        {
            // 成分内の密度マップ (挿入順を保持)
            var counts = new Dictionary<(long, long), int>();
            var order = new List<(long, long)>();
            foreach (int i in ids)
            {
                var key = (Fl(trunkPts[i].X / cfg.Cell), Fl(trunkPts[i].Z / cfg.Cell));
                if (!counts.ContainsKey(key)) { counts[key] = 0; order.Add(key); }
                counts[key]++;
            }

            // 局所極大セル (8近傍で最大かつ2点以上) を候補種に
            var peaks = new List<(double px, double pz, int n)>();
            foreach (var key in order)
            {
                int n = counts[key];
                if (n < 2) continue;
                bool isPeak = true;
                for (long da = -1; da <= 1 && isPeak; da++)
                {
                    for (long db = -1; db <= 1; db++)
                    {
                        if (da == 0 && db == 0) continue;
                        counts.TryGetValue((key.Item1 + da, key.Item2 + db), out int m);
                        if (m > n) { isPeak = false; break; }
                    }
                }
                if (isPeak) peaks.Add(((key.Item1 + 0.5) * cfg.Cell, (key.Item2 + 0.5) * cfg.Cell, n));
            }

            // 点数の多い順 (同数は元の順) に、既存種からPeakSep以上離れているものだけ採用
            var seeds = new List<(double px, double pz)>();
            foreach (var p in peaks.OrderByDescending(p => p.n)) // OrderByDescendingは安定ソート
            {
                bool far = true;
                foreach (var s in seeds)
                {
                    if (Math.Sqrt((p.px - s.px) * (p.px - s.px) + (p.pz - s.pz) * (p.pz - s.pz)) < cfg.PeakSep)
                    {
                        far = false;
                        break;
                    }
                }
                if (far) seeds.Add((p.px, p.pz));
            }
            if (seeds.Count == 0) return;

            // 各点を最寄り種に割り当て
            var groups = new List<List<int>>();
            for (int s = 0; s < seeds.Count; s++) groups.Add(new List<int>());
            foreach (int i in ids)
            {
                double x = trunkPts[i].X, z = trunkPts[i].Z;
                int best = 0;
                double bestD = double.MaxValue;
                for (int s = 0; s < seeds.Count; s++)
                {
                    double d = (x - seeds[s].px) * (x - seeds[s].px) + (z - seeds[s].pz) * (z - seeds[s].pz);
                    if (d < bestD) { bestD = d; best = s; }
                }
                groups[best].Add(i);
            }

            // サブクラスタごとに幹条件で評価
            foreach (var g in groups)
            {
                if (g.Count < cfg.MinPts) continue;
                double cx = 0, cz = 0, yMin = double.MaxValue, yMax = double.MinValue;
                foreach (int i in g)
                {
                    cx += trunkPts[i].X;
                    cz += trunkPts[i].Z;
                    yMin = Math.Min(yMin, trunkPts[i].Y);
                    yMax = Math.Max(yMax, trunkPts[i].Y);
                }
                cx /= g.Count;
                cz /= g.Count;

                double rMax = 0, vx = 0, vz = 0;
                foreach (int i in g)
                {
                    double dx = trunkPts[i].X - cx, dz = trunkPts[i].Z - cz;
                    rMax = Math.Max(rMax, Math.Sqrt(dx * dx + dz * dz));
                    vx += dx * dx;
                    vz += dz * dz;
                }
                double yExtent = yMax - yMin;
                if (rMax > cfg.MaxXzRadius || yExtent < cfg.MinYExtent) continue; // コンパクト・縦長条件

                double sx = Math.Sqrt(vx / g.Count), sz = Math.Sqrt(vz / g.Count); // 母標準偏差 (np.std既定)
                var tree = new MsTree
                {
                    X = cx,
                    Z = cz,
                    PointCount = g.Count,
                    YExtent = yExtent,
                    RadiusXz = rMax,
                    Width = 2.0 * Math.Sqrt(sx * sx + sz * sz),
                };
                foreach (int i in g) tree.PointIds.Add(idxMap[i]);
                outTrees.Add(tree);
            }
        }

        static long Fl(double v) => (long)Math.Floor(v);
    }
}
