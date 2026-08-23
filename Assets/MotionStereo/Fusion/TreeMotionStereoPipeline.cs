using System;
using System.Collections.Generic;
using UnityEngine;
using CanbatsuMS;
using CANbatsu.TreeDistance;

namespace CanbatsuMS
{
    /// <summary>
    /// エンドツーエンド統合パイプライン:
    ///   MotionStereoController(キャプチャ+実測距離)
    ///     → TreeDistanceEstimator(Depth Anything で木検出)
    ///     → TreeDistanceFusion(木ごとの距離を実測値で補正)
    ///
    /// 前提: 同じシーンに MotionStereoController と TreeDistanceEstimator があること
    /// (XREAL SDK と Sentis の両方が入ったプロジェクトで使う)。
    ///
    /// 使い方(フロント側):
    ///   pipeline.OnTreeResult += (result, report) => {
    ///       foreach (var tree in result.trees) { /* AR表示。tree.depth は実測補正済み */ }
    ///   };
    /// </summary>
    public class TreeMotionStereoPipeline : MonoBehaviour
    {
        [Tooltip("未設定なら同一GameObject/シーンから自動取得")]
        public MotionStereoController motionStereo;
        public TreeDistanceEstimator treeEstimator;

        [Header("融合設定")]
        [Tooltip("木の重心の周囲この半径(深度マップpx)にある実測点を割り当てる")]
        public float roiRadiusDepthMapPixels = 45f;
        [Tooltip("1本の木に割り当てる最小実測点数")]
        public int minPointsPerTree = 3;
        [Tooltip("実測点が当たらなかった木にもグローバルスケール補正を適用する")]
        public bool applyGlobalScaleToUnmatched = true;

        /// <summary>木検出+距離補正の完了イベント(メインスレッド)</summary>
        public event Action<TreeAnalysisResult, TreeDistanceFusion.FusionReport> OnTreeResult;

        private Texture2D _reusableTex;

        void Start()
        {
            // 本番ではInspector/コードで明示参照を張ること。自動解決はプロトタイプ向けの便宜 (統合メモ3-5)
            if (motionStereo == null) motionStereo = FindFirstObjectByType<MotionStereoController>();
            if (treeEstimator == null) treeEstimator = FindFirstObjectByType<TreeDistanceEstimator>();
            if (motionStereo == null || treeEstimator == null)
            {
                Debug.LogError("[TreePipeline] MotionStereoController / TreeDistanceEstimator がシーンにありません");
                enabled = false;
                return;
            }
            motionStereo.OnResult += OnMotionStereoResult;
        }

        void OnDestroy()
        {
            if (motionStereo != null)
                motionStereo.OnResult -= OnMotionStereoResult;
        }

        private void OnMotionStereoResult(MsResult ms)
        {
            if (!ms.Success || motionStereo.LastKeyframeA == null)
            {
                Debug.LogWarning("[TreePipeline] モーションステレオ失敗のため木検出をスキップ: " + ms.Message);
                return;
            }
            var kfA = motionStereo.LastKeyframeA;

            // 1. キーフレームA を Texture2D 化して Depth Anything で木検出(メインスレッド必須)
            var tex = KeyframeToTexture(kfA);
            TreeAnalysisResult result = treeEstimator.Analyze(tex);

            // 2. 実測点 (u,v,距離) に変換して融合
            var pts = new List<Vector3>(ms.Points.Count);
            foreach (var p in ms.Points)
                pts.Add(new Vector3(p.U, p.V, p.DistanceMeters));

            var cfg = new TreeDistanceFusion.FusionConfig
            {
                roiRadiusDepthMapPixels = roiRadiusDepthMapPixels,
                minPointsPerTree = minPointsPerTree,
                applyGlobalScaleToUnmatched = applyGlobalScaleToUnmatched,
                maxUsableDepthMeters = treeEstimator.maxUsableDepthMeters,
                closePairDistanceThresholdMeters = treeEstimator.closePairDistanceThresholdMeters,
                minTrunkDiameterMeters = treeEstimator.minTrunkDiameterMeters,
            };
            var report = TreeDistanceFusion.Refine(
                result, pts, kfA.Width, kfA.Height,
                treeEstimator.ModelInputWidth, treeEstimator.ModelInputHeight, cfg);

            Debug.Log($"[TreePipeline] 木={result.trees.Count}本 近接ペア={result.closePairs.Count} 融合: {report}");
            OnTreeResult?.Invoke(result, report);
        }

        /// <summary>
        /// MsKeyframe(グレースケール, row0=上端)を Texture2D(RGB, Unity標準のbottom-up)へ変換。
        /// Depth Anything はグレー3chでも動作する(精度検証は実写で実施予定)。
        /// </summary>
        private Texture2D KeyframeToTexture(MsKeyframe kf)
        {
            if (_reusableTex == null || _reusableTex.width != kf.Width || _reusableTex.height != kf.Height)
                _reusableTex = new Texture2D(kf.Width, kf.Height, TextureFormat.RGB24, false);
            var pixels = new Color32[kf.Width * kf.Height];
            for (int y = 0; y < kf.Height; y++)
            {
                int srcRow = y * kf.Width;                       // kf.Image: row0=上端
                int dstRow = (kf.Height - 1 - y) * kf.Width;     // Texture2D: row0=下端
                for (int x = 0; x < kf.Width; x++)
                {
                    byte g = (byte)kf.Image[srcRow + x];
                    pixels[dstRow + x] = new Color32(g, g, g, 255);
                }
            }
            _reusableTex.SetPixels32(pixels);
            _reusableTex.Apply();
            return _reusableTex;
        }
    }
}
