using System.Collections.Generic;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>
    /// モーションステレオの1キーフレーム: グレースケール画像 + カメラ姿勢 + 内部パラメータ。
    /// 画像は row0=上端 の通常順で保持する(Unityテクスチャのbottom-upは取り込み時に反転)。
    /// </summary>
    public class MsKeyframe
    {
        public float[] Image;      // 輝度 (w*h, row-major, row0=上端)
        public int Width;
        public int Height;
        public float Fx, Fy, Cx, Cy;
        public Vector3 CamPosition;    // カメラ原点(ワールド)
        public Quaternion CamRotation; // カメラ→ワールド回転

        /// <summary>
        /// XREALRGBCameraTexture の Yプレーン(bottom-up)と頭姿勢からキーフレームを作る。
        /// </summary>
        public static MsKeyframe FromYPlane(
            byte[] yPlaneBottomUp, int width, int height,
            float fx, float fy, float cx, float cy,
            Vector3 headPos, Quaternion headRot,
            Vector3 camOffsetPos, Quaternion camOffsetRot)
        {
            var kf = new MsKeyframe
            {
                Width = width,
                Height = height,
                Fx = fx, Fy = fy, Cx = cx, Cy = cy,
                CamPosition = headPos + headRot * camOffsetPos,
                CamRotation = headRot * camOffsetRot,
                Image = new float[width * height],
            };
            // bottom-up → top-down 反転しつつ float 化
            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y) * width;
                int dstRow = y * width;
                for (int x = 0; x < width; x++)
                    kf.Image[dstRow + x] = yPlaneBottomUp[srcRow + x];
            }
            return kf;
        }
    }

    /// <summary>三角測量された1点</summary>
    public struct MsDepthPoint
    {
        public float U, V;         // キーフレームA上の画素
        public float DistanceMeters; // カメラAからのユークリッド距離(スケール補正後)
    }

    /// <summary>注目領域内の深度クラスタ</summary>
    public struct MsCluster
    {
        public float MedianMeters;
        public int PointCount;
        public float MinMeters, MaxMeters;
    }

    /// <summary>推定結果</summary>
    public class MsResult
    {
        public bool Success;
        public string Message = "";
        public float BaselineMeters;      // 6DoFによる基線長
        public float ScaleFactor = 1f;    // 適用したスケール補正
        public List<MsDepthPoint> Points = new List<MsDepthPoint>();
        public List<MsCluster> RoiClusters = new List<MsCluster>();
        /// <summary>注目領域の最前面クラスタ中央値 = 対象までの距離。失敗時 -1</summary>
        public float TargetDistanceMeters = -1f;
        public int TargetPointCount;
        public long ElapsedMs;
    }

    /// <summary>アルゴリズム設定(既定値は実データ検証済み: 5m実測で誤差-0.4%)</summary>
    public class MsConfig
    {
        // コーナー検出
        public int CornerCellSize = 20;
        public int CornerMargin = 20;
        public float CornerMinScore = 200f;
        // LK
        public int PyramidLevels = 4;
        public int PatchRadius = 7;
        public int LkIterations = 12;
        public float FwdBwdMaxPixels = 1.0f;
        public float[] InitDepthHypotheses = { 2f, 4f, 8f };
        public float MinNcc = 0.6f;
        // 三角測量
        public float MaxRaySkewMeters = 0.5f;
        public float MinDistance = 0.3f;
        public float MaxDistance = 50f;
        // クラスタリング
        public float ClusterGapMeters = 0.6f;
        public float ClusterMinFraction = 0.2f; // 領域内点数のこの割合以上で有効クラスタ
        public int ClusterMinPoints = 4;
        // 注目領域半径(画像幅比)
        public float RoiRadiusFraction = 0.12f;
    }
}
