using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>
    /// 旧セッション座標系→現セッション座標系の2D剛体変換 (XZ平面の回転+並進。スケールは1固定 =
    /// 6DoFは実寸なので伸縮しない)。Yは地面高さの差を別途入れる。
    /// </summary>
    public struct MsMapTransform
    {
        public float Theta;    // 回転 [rad] (XZ平面、+YまわりのUnity左手系での見かけ回転)
        public float Tx, Tz;   // 並進 [m]
        public float Ty;       // 高さオフセット [m] (呼び出し側が地面推定の差を入れる)

        public Vector3 Apply(Vector3 p)
        {
            float c = (float)Math.Cos(Theta), s = (float)Math.Sin(Theta);
            return new Vector3(
                c * p.x - s * p.z + Tx,
                p.y + Ty,
                s * p.x + c * p.z + Tz);
        }

        public void ApplyXZ(float x, float z, out float ox, out float oz)
        {
            float c = (float)Math.Cos(Theta), s = (float)Math.Sin(Theta);
            ox = c * x - s * z + Tx;
            oz = s * x + c * z + Tz;
        }
    }

    /// <summary>照合結果 (成否と確からしさを呼び出し側が判断できるように返す)</summary>
    public class MsAlignResult
    {
        public bool Success;
        public MsMapTransform Transform;
        public int InlierCount;      // 対応づいた木の本数
        public float RmsMeters;      // 対応点の残差RMS
        public string Message = "";
    }

    /// <summary>
    /// 幹マップ照合: 「木は動かない」ことを利用し、保存済みの幹のXZ配置パターンと
    /// 現セッションで検出した幹の配置を2D剛体変換で重ね合わせる (再訪時のマーカー復元用)。
    ///
    /// 方式: 幹2本ペアの距離が一致する組をRANSAC的に試し、2点対応から変換を仮決め→
    /// 全体の最近傍一致数で採点→最良候補を最小二乗 (Procrustes) で反復refine。
    /// 基準点合わせ (coarseInit) があれば探索をその近傍に絞り、対称配置の曖昧さも解ける。
    /// 純計算のみ (ワーカースレッド可)。乱数は使わず決定的。
    /// </summary>
    public static class TreeMapAligner
    {
        public class Config
        {
            public float PairDistTol = 0.30f;    // ペア間距離の一致許容 [m]
            public float InlierRadius = 0.50f;   // 対応点とみなす最近傍距離 [m]
            public int MinInliers = 3;           // 成功に必要な最小対応本数
            public float MaxRms = 0.35f;         // 成功に許す最大RMS [m]
            public float MinPairSep = 1.2f;      // 仮説に使うペアの最小間隔 (近いと角度が不安定)
            public float MaxPairSep = 20f;       // 同・最大間隔
            public int MaxHypotheses = 2000;     // 試す仮説数の上限 (決定的に先頭から)
            // coarseInit がある場合の探索窓
            public float CoarseThetaWindowDeg = 45f;
            public float CoarseTransWindow = 8f;
        }

        /// <summary>
        /// oldTrees (旧セッションの幹XZ) を newTrees (現セッションの幹XZ) に重ねる変換を推定する。
        /// Vector2 は (x=ワールドX, y=ワールドZ)。
        /// </summary>
        public static MsAlignResult Align(
            IReadOnlyList<Vector2> oldTrees, IReadOnlyList<Vector2> newTrees,
            MsMapTransform? coarseInit = null, Config cfg = null)
        {
            cfg = cfg ?? new Config();
            var res = new MsAlignResult();
            if (oldTrees == null || newTrees == null || oldTrees.Count < 2 || newTrees.Count < 2)
            {
                res.Message = "木が少なすぎて照合できない (双方2本以上必要)";
                return res;
            }

            // ---- 1. 仮説列挙: 距離の合う「旧2本 vs 新2本」から変換を仮決め ----
            // 旧ペアは間隔の長い順 (角度が安定する順) に決定的に走査する
            var oldPairs = new List<(int i, int j, float d)>();
            for (int i = 0; i < oldTrees.Count; i++)
                for (int j = i + 1; j < oldTrees.Count; j++)
                {
                    float d = Dist(oldTrees[i], oldTrees[j]);
                    if (d >= cfg.MinPairSep && d <= cfg.MaxPairSep) oldPairs.Add((i, j, d));
                }
            oldPairs.Sort((a, b) => b.d.CompareTo(a.d));

            var newPairs = new List<(int k, int l, float d)>();
            for (int k = 0; k < newTrees.Count; k++)
                for (int l = k + 1; l < newTrees.Count; l++)
                {
                    float d = Dist(newTrees[k], newTrees[l]);
                    if (d >= cfg.MinPairSep && d <= cfg.MaxPairSep) newPairs.Add((k, l, d));
                }

            int bestInliers = 0;
            float bestRms = float.MaxValue;
            MsMapTransform best = default;
            int hypotheses = 0;

            foreach (var op in oldPairs)
            {
                if (hypotheses >= cfg.MaxHypotheses) break;
                foreach (var np in newPairs)
                {
                    if (hypotheses >= cfg.MaxHypotheses) break;
                    if (Math.Abs(op.d - np.d) > cfg.PairDistTol) continue;

                    // 対応の向き2通り (i→k,j→l) と (i→l,j→k)
                    for (int flip = 0; flip < 2; flip++)
                    {
                        var a = oldTrees[op.i]; var b = oldTrees[op.j];
                        var p = flip == 0 ? newTrees[np.k] : newTrees[np.l];
                        var q = flip == 0 ? newTrees[np.l] : newTrees[np.k];

                        float theta = (float)(Math.Atan2(q.y - p.y, q.x - p.x)
                                            - Math.Atan2(b.y - a.y, b.x - a.x));
                        float c = (float)Math.Cos(theta), s = (float)Math.Sin(theta);
                        var t = new MsMapTransform
                        {
                            Theta = theta,
                            Tx = p.x - (c * a.x - s * a.y),
                            Tz = p.y - (s * a.x + c * a.y),
                        };
                        hypotheses++;

                        if (coarseInit.HasValue && !WithinCoarse(t, coarseInit.Value, cfg)) continue;

                        Score(oldTrees, newTrees, t, cfg.InlierRadius, out int inl, out float rms, null);
                        if (inl > bestInliers || (inl == bestInliers && rms < bestRms))
                        {
                            bestInliers = inl; bestRms = rms; best = t;
                        }
                    }
                }
            }

            if (bestInliers < 2)
            {
                res.Message = $"一致する配置が見つからない (仮説{hypotheses}件)";
                return res;
            }

            // ---- 2. 最良仮説を対応点の最小二乗 (Procrustes) で反復refine ----
            var matches = new List<(int o, int n)>();
            for (int it = 0; it < 3; it++)
            {
                matches.Clear();
                Score(oldTrees, newTrees, best, cfg.InlierRadius, out bestInliers, out bestRms, matches);
                if (matches.Count < 2) break;
                best = FitRigid(oldTrees, newTrees, matches, best);
            }
            Score(oldTrees, newTrees, best, cfg.InlierRadius, out bestInliers, out bestRms, null);

            res.Transform = best;
            res.InlierCount = bestInliers;
            res.RmsMeters = bestRms;
            res.Success = bestInliers >= cfg.MinInliers && bestRms <= cfg.MaxRms;
            res.Message = res.Success
                ? $"照合OK: {bestInliers}本一致 RMS {bestRms:F2}m"
                : $"確度不足: {bestInliers}本一致 RMS {bestRms:F2}m";
            return res;
        }

        static bool WithinCoarse(MsMapTransform t, MsMapTransform coarse, Config cfg)
        {
            float dTheta = NormalizeAngle(t.Theta - coarse.Theta);
            if (Math.Abs(dTheta) > cfg.CoarseThetaWindowDeg * Math.PI / 180.0) return false;
            float dx = t.Tx - coarse.Tx, dz = t.Tz - coarse.Tz;
            return dx * dx + dz * dz <= cfg.CoarseTransWindow * cfg.CoarseTransWindow;
        }

        static float NormalizeAngle(float a)
        {
            while (a > Math.PI) a -= (float)(2 * Math.PI);
            while (a < -Math.PI) a += (float)(2 * Math.PI);
            return a;
        }

        /// <summary>変換tで旧→新へ写し、新の木との貪欲1対1最近傍対応で採点する</summary>
        static void Score(IReadOnlyList<Vector2> oldTrees, IReadOnlyList<Vector2> newTrees,
                          MsMapTransform t, float inlierRadius,
                          out int inliers, out float rms, List<(int o, int n)> matchesOut)
        {
            // 旧の各木を写して、最近傍距離が小さい順に1対1で割り当てる (二重割り当て防止)
            var cand = new List<(float d2, int o, int n)>();
            float r2 = inlierRadius * inlierRadius;
            for (int o = 0; o < oldTrees.Count; o++)
            {
                t.ApplyXZ(oldTrees[o].x, oldTrees[o].y, out float mx, out float mz);
                for (int n = 0; n < newTrees.Count; n++)
                {
                    float dx = newTrees[n].x - mx, dz = newTrees[n].y - mz;
                    float d2 = dx * dx + dz * dz;
                    if (d2 <= r2) cand.Add((d2, o, n));
                }
            }
            cand.Sort((a, b) => a.d2.CompareTo(b.d2));
            var usedO = new bool[oldTrees.Count];
            var usedN = new bool[newTrees.Count];
            inliers = 0;
            double sum = 0;
            foreach (var c in cand)
            {
                if (usedO[c.o] || usedN[c.n]) continue;
                usedO[c.o] = true; usedN[c.n] = true;
                inliers++;
                sum += c.d2;
                matchesOut?.Add((c.o, c.n));
            }
            rms = inliers > 0 ? (float)Math.Sqrt(sum / inliers) : float.MaxValue;
        }

        /// <summary>対応点集合から2D剛体変換を最小二乗で解く (スケールなしProcrustes)</summary>
        static MsMapTransform FitRigid(IReadOnlyList<Vector2> oldTrees, IReadOnlyList<Vector2> newTrees,
                                       List<(int o, int n)> matches, MsMapTransform fallback)
        {
            if (matches.Count < 2) return fallback;
            double ocx = 0, ocz = 0, ncx = 0, ncz = 0;
            foreach (var m in matches)
            {
                ocx += oldTrees[m.o].x; ocz += oldTrees[m.o].y;
                ncx += newTrees[m.n].x; ncz += newTrees[m.n].y;
            }
            ocx /= matches.Count; ocz /= matches.Count;
            ncx /= matches.Count; ncz /= matches.Count;

            double sxx = 0, sxy = 0;
            foreach (var m in matches)
            {
                double ox = oldTrees[m.o].x - ocx, oz = oldTrees[m.o].y - ocz;
                double nx = newTrees[m.n].x - ncx, nz = newTrees[m.n].y - ncz;
                sxx += ox * nx + oz * nz;   // Σ dot
                sxy += ox * nz - oz * nx;   // Σ cross
            }
            float theta = (float)Math.Atan2(sxy, sxx);
            float c = (float)Math.Cos(theta), s = (float)Math.Sin(theta);
            return new MsMapTransform
            {
                Theta = theta,
                Tx = (float)(ncx - (c * ocx - s * ocz)),
                Tz = (float)(ncz - (s * ocx + c * ocz)),
                Ty = fallback.Ty,
            };
        }

        static float Dist(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
