using System.Collections.Generic;
using UnityEngine;

namespace CANbatsu.TreeDistance
{
    /// <summary>
    /// モーションステレオの実測距離で TreeAnalysisResult(Depth Anything の結果)を補正する。
    ///
    /// 背景: Depth Anything は近距離で誤差が大きい(2mで+140%)。モーションステレオは
    /// 誤差2%級だが疎(コーナー点のみ)。そこで:
    ///   1. 各木クラスタの重心付近にある実測点を集め、最前面クラスタの中央値でその木の距離を上書き
    ///   2. 実測点が当たらなかった木には、当たった木々の補正倍率の中央値(グローバルスケール)を適用
    ///   3. 距離に線形な量(position3D・幹太さ)を同倍率で補正し、isThin/closePairs を再判定
    ///
    /// 依存を切るため、実測点は (u, v, 距離m) の Vector3 リストで受け取る
    /// (CanbatsuMS.MsResult.Points からの変換は TreeMotionStereoPipeline 参照)。
    /// </summary>
    public static class TreeDistanceFusion
    {
        public class FusionConfig
        {
            [Tooltip("木の重心の周囲この半径(深度マップpx)にある実測点を割り当てる")]
            public float roiRadiusDepthMapPixels = 45f;
            [Tooltip("実測点の深度クラスタ分割の切れ目(m)")]
            public float clusterGapMeters = 0.6f;
            [Tooltip("1本の木に割り当てる最小実測点数(未満ならその木は直接補正しない)")]
            public int minPointsPerTree = 3;
            [Tooltip("実測点が当たらなかった木にグローバルスケール補正を適用するか")]
            public bool applyGlobalScaleToUnmatched = true;
            [Tooltip("補正後にこの距離(m)を超える木を除外する")]
            public float maxUsableDepthMeters = 10f;
            [Tooltip("近すぎ判定の距離閾値(m)")]
            public float closePairDistanceThresholdMeters = 1.0f;
            [Tooltip("細すぎ判定の幹直径閾値(m)")]
            public float minTrunkDiameterMeters = 0.15f;
        }

        public class FusionReport
        {
            public int treesRefined;     // 実測点で直接補正した木の数
            public int treesScaledOnly;  // グローバルスケールのみ適用した木の数
            public int treesRemoved;     // 補正後フィルタで除外された木の数
            public float globalScale = 1f;
            public bool globalScaleValid;
            public override string ToString() =>
                $"直接補正={treesRefined} スケールのみ={treesScaledOnly} 除外={treesRemoved} " +
                $"グローバルスケール={(globalScaleValid ? globalScale.ToString("F3") : "N/A")}";
        }

        /// <summary>
        /// result を実測距離で補正する(result は書き換えられる)。
        /// </summary>
        /// <param name="result">TreeDistanceEstimator.Analyze() の出力</param>
        /// <param name="msPoints">実測点 (x=画素u, y=画素v, z=距離m)。座標は元カメラ画像基準</param>
        /// <param name="msImageWidth">実測点の画像幅(例: 1280)</param>
        /// <param name="msImageHeight">実測点の画像高さ(例: 720)</param>
        /// <param name="depthMapWidth">深度マップ幅(例: 518)</param>
        /// <param name="depthMapHeight">深度マップ高さ(例: 518)</param>
        public static FusionReport Refine(
            TreeAnalysisResult result,
            IReadOnlyList<Vector3> msPoints,
            int msImageWidth, int msImageHeight,
            int depthMapWidth, int depthMapHeight,
            FusionConfig cfg = null)
        {
            cfg = cfg ?? new FusionConfig();
            var report = new FusionReport();
            if (result == null || result.trees == null || result.trees.Count == 0 ||
                msPoints == null || msPoints.Count == 0)
                return report;

            // 実測点を深度マップ座標系へ(どちらも row0=上端 なので単純スケール)
            float sx = (float)depthMapWidth / msImageWidth;
            float sy = (float)depthMapHeight / msImageHeight;
            var mapped = new List<Vector3>(msPoints.Count);
            foreach (var p in msPoints)
                mapped.Add(new Vector3(p.x * sx, p.y * sy, p.z));

            // 1. 各木へ実測点を割り当てて直接補正
            var ratios = new List<float>();
            var refined = new bool[result.trees.Count];
            float r2 = cfg.roiRadiusDepthMapPixels * cfg.roiRadiusDepthMapPixels;
            for (int i = 0; i < result.trees.Count; i++)
            {
                var tree = result.trees[i];
                var near = new List<float>();
                foreach (var p in mapped)
                {
                    float du = p.x - tree.pixelPosition.x;
                    float dv = p.y - tree.pixelPosition.y;
                    if (du * du + dv * dv < r2)
                        near.Add(p.z);
                }
                if (near.Count < cfg.minPointsPerTree)
                    continue;

                float msDist = FrontClusterMedian(near, cfg.clusterGapMeters, cfg.minPointsPerTree);
                if (msDist <= 0f || tree.depth <= 0f)
                    continue;

                float ratio = msDist / tree.depth;
                ApplyRatio(tree, ratio, cfg);
                ratios.Add(ratio);
                refined[i] = true;
                report.treesRefined++;
            }

            // 2. グローバルスケール(直接補正できた木の倍率の中央値)
            if (ratios.Count > 0)
            {
                ratios.Sort();
                report.globalScale = ratios.Count % 2 == 1
                    ? ratios[ratios.Count / 2]
                    : (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]) * 0.5f;
                report.globalScaleValid = true;

                if (cfg.applyGlobalScaleToUnmatched)
                {
                    for (int i = 0; i < result.trees.Count; i++)
                    {
                        if (refined[i]) continue;
                        ApplyRatio(result.trees[i], report.globalScale, cfg);
                        report.treesScaledOnly++;
                    }
                }
            }

            // 3. 補正後の距離で再フィルタ → ペア・フラグ再計算
            var kept = new List<TreeCandidate>(result.trees.Count);
            foreach (var t in result.trees)
            {
                if (t.depth <= cfg.maxUsableDepthMeters) kept.Add(t);
                else report.treesRemoved++;
            }
            result.trees = kept;

            result.closePairs = new List<ClosePair>();
            foreach (var t in result.trees)
                t.isTooClose = false;
            for (int i = 0; i < result.trees.Count; i++)
            {
                for (int j = i + 1; j < result.trees.Count; j++)
                {
                    float d = Vector3.Distance(result.trees[i].position3D, result.trees[j].position3D);
                    if (d < cfg.closePairDistanceThresholdMeters)
                    {
                        result.closePairs.Add(new ClosePair(i, j, d));
                        result.trees[i].isTooClose = true;
                        result.trees[j].isTooClose = true;
                    }
                }
            }
            foreach (var t in result.trees)
                t.highlightRed = t.isThin || t.isTooClose;

            return report;
        }

        /// <summary>距離に線形な量を倍率で補正し、isThin を再判定する</summary>
        private static void ApplyRatio(TreeCandidate tree, float ratio, FusionConfig cfg)
        {
            tree.depth *= ratio;
            tree.position3D = tree.position3D * ratio; // 同一レイ上のスケーリング
            if (tree.trunkWidthMeters > 0f)
            {
                tree.trunkWidthMeters *= ratio;
                tree.isThin = tree.trunkWidthMeters < cfg.minTrunkDiameterMeters;
            }
        }

        /// <summary>深度リストをギャップ分割し、最前面の有効クラスタの中央値を返す(幹は背景より手前)</summary>
        private static float FrontClusterMedian(List<float> depths, float gap, int minPts)
        {
            depths.Sort();
            var clusters = new List<List<float>>();
            var cur = new List<float> { depths[0] };
            for (int i = 1; i < depths.Count; i++)
            {
                if (depths[i] - cur[cur.Count - 1] > gap)
                {
                    clusters.Add(cur);
                    cur = new List<float>();
                }
                cur.Add(depths[i]);
            }
            clusters.Add(cur);

            int need = Mathf.Max(minPts, (int)(0.2f * depths.Count));
            List<float> target = null;
            List<float> largest = clusters[0];
            foreach (var c in clusters)
            {
                if (c.Count > largest.Count) largest = c;
                if (target == null && c.Count >= need) target = c; // 最前面(ソート済み)
            }
            if (target == null) target = largest;
            int n = target.Count;
            return n % 2 == 1 ? target[n / 2] : (target[n / 2 - 1] + target[n / 2]) * 0.5f;
        }
    }
}
