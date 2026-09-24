using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>
    /// 曲がり木分類 (AI担当の bend_classifier)。幹検出 (TrunkDetectorML) が出した箱を
    /// 切り出して「曲がっているか」のスコア (0〜1) を返す。
    /// 前処理は配布スクリプト detect_bent.py と同一:
    ///   箱の横幅が画像幅の 31/1280 以上のときだけ判定 / 横に10%のマージン / 256x256へ縮小 / /255
    /// モデル: Assets/Resources/AR/bend_classifier.onnx (YOLO分類、出力=クラス確率ベクトル)。
    /// 入力はキーフレームの輝度画像 (グレー3ch複製。幹検出と同じ供給経路)。
    /// </summary>
    public class BendClassifierML : IDisposable
    {
        /// <summary>この確率以上を「曲がり候補」とする (配布スクリプトの既定0.2。0.15=多め, 0.5=少なめ)</summary>
        public const float Threshold = 0.2f;

        const int IN = 256;
        const float MinWidthFrac = 31f / 1280f; // これより細い箱は判定しない (遠すぎて分類が当てにならない)
        const float Margin = 0.10f;             // 箱の横に足すマージン (幹の輪郭が切れないように)

        // "bent" クラスのインデックス。ONNX書き出し時のクラス表 (names) に合わせる。
        // 書き出しログで確認した順序をここに固定する (docs/段階的再導入計画の段階4.6参照)
        public const int BentClassIndex = 0;

        private Worker _worker;
        private readonly float[] _input = new float[3 * IN * IN];

        public static BendClassifierML TryCreate()
        {
            var asset = Resources.Load<ModelAsset>("AR/bend_classifier");
            if (asset == null)
            {
                Debug.Log("[BendML] Resources/AR/bend_classifier.onnx が無いので曲がり判定なしで続行");
                return null;
            }
            try
            {
                var c = new BendClassifierML();
                var model = ModelLoader.Load(asset);
                var backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
                c._worker = new Worker(model, backend);
                Debug.Log($"[BendML] モデル読込OK backend={backend}");
                return c;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BendML] 初期化失敗: " + e.Message);
                return null;
            }
        }

        /// <summary>初回推論のGPU準備を済ませる (作業開始直後に1回呼ぶ)</summary>
        public void WarmUp()
        {
            try
            {
                using var input = new Tensor<float>(new TensorShape(1, 3, IN, IN), _input);
                _worker.Schedule(input);
                (_worker.PeekOutput() as Tensor<float>)?.DownloadToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BendML] warmup失敗: " + e.Message);
            }
        }

        /// <summary>
        /// 幹の箱の曲がりスコアを返す。判定対象外 (細すぎる箱・失敗) は -1。
        /// </summary>
        public float Classify(MsKeyframe kf, TrunkBox box)
        {
            if (_worker == null || kf?.Image == null) return -1f;
            int W = kf.Width, H = kf.Height;
            float bw = box.U1 - box.U0;
            if (bw / W < MinWidthFrac) return -1f;

            // 切り出し範囲 (横マージン付き、画像内にクランプ)
            float mx = bw * Margin;
            float cx0 = Mathf.Max(0f, box.U0 - mx);
            float cx1 = Mathf.Min(W - 1f, box.U1 + mx);
            float cy0 = Mathf.Max(0f, box.V0);
            float cy1 = Mathf.Min(H - 1f, box.V1);
            float cw = cx1 - cx0, ch = cy1 - cy0;
            if (cw < 4f || ch < 4f) return -1f;

            // バイリニアで256x256へ (グレー→3ch複製、/255)
            const int plane = IN * IN;
            for (int y = 0; y < IN; y++)
            {
                float sy = cy0 + (y + 0.5f) * ch / IN - 0.5f;
                int y0 = Mathf.Clamp((int)sy, 0, H - 1);
                int y1 = Mathf.Min(y0 + 1, H - 1);
                float fy = Mathf.Clamp01(sy - y0);
                int dstRow = y * IN;
                for (int x = 0; x < IN; x++)
                {
                    float sx = cx0 + (x + 0.5f) * cw / IN - 0.5f;
                    int x0 = Mathf.Clamp((int)sx, 0, W - 1);
                    int x1 = Mathf.Min(x0 + 1, W - 1);
                    float fx = Mathf.Clamp01(sx - x0);
                    float v0 = kf.Image[y0 * W + x0] * (1 - fx) + kf.Image[y0 * W + x1] * fx;
                    float v1 = kf.Image[y1 * W + x0] * (1 - fx) + kf.Image[y1 * W + x1] * fx;
                    float v = (v0 * (1 - fy) + v1 * fy) / 255f;
                    int di = dstRow + x;
                    _input[di] = v;
                    _input[plane + di] = v;
                    _input[2 * plane + di] = v;
                }
            }

            try
            {
                float[] o;
                using (var input = new Tensor<float>(new TensorShape(1, 3, IN, IN), _input))
                {
                    _worker.Schedule(input);
                    var outT = _worker.PeekOutput() as Tensor<float>;
                    if (outT == null) return -1f;
                    o = outT.DownloadToArray();
                }
                if (o.Length <= BentClassIndex) return -1f;

                // 出力が確率 (合計≒1) でなければソフトマックスをかける (書き出し方の差異に両対応)
                float sum = 0f;
                for (int i = 0; i < o.Length; i++) sum += o[i];
                if (sum < 0.99f || sum > 1.01f)
                {
                    float max = float.MinValue;
                    for (int i = 0; i < o.Length; i++) max = Mathf.Max(max, o[i]);
                    float expSum = 0f;
                    for (int i = 0; i < o.Length; i++)
                    {
                        o[i] = Mathf.Exp(o[i] - max);
                        expSum += o[i];
                    }
                    for (int i = 0; i < o.Length; i++) o[i] /= expSum;
                }
                return o[BentClassIndex];
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BendML] 推論失敗: " + e.Message);
                return -1f;
            }
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _worker = null;
        }
    }
}
