using System;
using System.Collections.Generic;
using Unity.InferenceEngine; // Unity 6.4: Sentis改め Inference Engine (APIは同一)
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>ML検出された幹の箱(キーフレーム画素座標)</summary>
    public struct TrunkBox
    {
        public float U0, V0, U1, V1;
        public float Conf;
    }

    /// <summary>
    /// YOLO11n(trunk_mix)による幹検出。Unity Sentis 2.1 で ONNX を実行する。
    ///   - モデル: Assets/Resources/AR/trunk_mix.onnx(入力 1x3x640x640、出力 1x5x8400)
    ///   - 学習: Grounding DINO 自動ラベル + 人手アノテの混合(friend評価 F1 0.756)
    ///   - 入力はキーフレームの輝度画像(グレースケールを3chに複製、レターボックス640x640)
    /// 推論はメインスレッドで約1回/計測ペア(0.5秒間隔程度)なので同期実行で足りる。
    /// </summary>
    public class TrunkDetectorML : IDisposable
    {
        public float ConfThreshold = 0.35f;
        public float NmsIou = 0.6f;

        private const int IN = 640;
        private Worker _worker;

        /// <summary>Resources/AR/trunk_mix.onnx から生成。失敗時は null(呼び側で幾何方式へフォールバック)</summary>
        public static TrunkDetectorML TryCreate()
        {
            var asset = Resources.Load<ModelAsset>("AR/trunk_mix");
            if (asset == null)
            {
                Debug.LogWarning("[TrunkML] Resources/AR/trunk_mix.onnx が見つかりません(幾何方式で継続)");
                return null;
            }
            try
            {
                var det = new TrunkDetectorML();
                var model = ModelLoader.Load(asset);
                var backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
                det._worker = new Worker(model, backend);
                Debug.Log($"[TrunkML] モデル読込OK backend={backend}");
                return det;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrunkML] 初期化失敗: " + e.Message);
                return null;
            }
        }

        /// <summary>キーフレーム画像から幹の箱を検出(画素座標で返す)</summary>
        public List<TrunkBox> Detect(MsKeyframe kf)
        {
            var result = new List<TrunkBox>();
            if (_worker == null || kf?.Image == null) return result;
            int w = kf.Width, h = kf.Height;

            // ---- 1. レターボックス 640x640(グレー→3ch複製、/255) ----
            float scale = Mathf.Min((float)IN / w, (float)IN / h);
            int rw = Mathf.RoundToInt(w * scale), rh = Mathf.RoundToInt(h * scale);
            int padX = (IN - rw) / 2, padY = (IN - rh) / 2;
            var data = new float[3 * IN * IN]; // ゼロ初期化 = 黒パディング
            const int plane = IN * IN;
            for (int y = 0; y < rh; y++)
            {
                float sy = (y + 0.5f) / scale - 0.5f;
                int y0 = Mathf.Clamp((int)sy, 0, h - 1);
                int y1 = Mathf.Min(y0 + 1, h - 1);
                float fy2 = Mathf.Clamp01(sy - y0);
                int dstRow = (padY + y) * IN + padX;
                for (int x = 0; x < rw; x++)
                {
                    float sx = (x + 0.5f) / scale - 0.5f;
                    int x0 = Mathf.Clamp((int)sx, 0, w - 1);
                    int x1 = Mathf.Min(x0 + 1, w - 1);
                    float fx2 = Mathf.Clamp01(sx - x0);
                    float v0 = kf.Image[y0 * w + x0] * (1 - fx2) + kf.Image[y0 * w + x1] * fx2;
                    float v1 = kf.Image[y1 * w + x0] * (1 - fx2) + kf.Image[y1 * w + x1] * fx2;
                    float v = (v0 * (1 - fy2) + v1 * fy2) / 255f;
                    int di = dstRow + x;
                    data[di] = v;
                    data[plane + di] = v;
                    data[2 * plane + di] = v;
                }
            }

            // ---- 2. 推論(同期) ----
            float[] o;
            using (var input = new Tensor<float>(new TensorShape(1, 3, IN, IN), data))
            {
                _worker.Schedule(input);
                var outT = _worker.PeekOutput() as Tensor<float>;
                if (outT == null)
                {
                    Debug.LogWarning("[TrunkML] 出力テンソル取得失敗");
                    return result;
                }
                o = outT.DownloadToArray(); // (1,5,N) → 長さ 5N
            }

            // ---- 3. デコード + NMS(cx,cy,w,h,conf / 640座標系 → 画素座標へ逆写像) ----
            int n = o.Length / 5;
            var cands = new List<TrunkBox>();
            for (int i = 0; i < n; i++)
            {
                float conf = o[4 * n + i];
                if (conf < ConfThreshold) continue;
                float cx = o[i], cy = o[n + i], bw = o[2 * n + i], bh = o[3 * n + i];
                cands.Add(new TrunkBox
                {
                    U0 = (cx - bw / 2 - padX) / scale,
                    V0 = (cy - bh / 2 - padY) / scale,
                    U1 = (cx + bw / 2 - padX) / scale,
                    V1 = (cy + bh / 2 - padY) / scale,
                    Conf = conf,
                });
            }
            cands.Sort((a, b) => b.Conf.CompareTo(a.Conf));
            foreach (var c in cands)
            {
                bool keep = true;
                foreach (var k in result)
                {
                    if (IoU(c, k) > NmsIou) { keep = false; break; }
                }
                if (keep) result.Add(c);
            }
            return result;
        }

        /// <summary>箱の中央帯(高さ40〜72%)の複数行で輝度エッジから幹の画素幅を測る。
        /// MLの箱は枝や傾きで幹より広めに出るため、径の算出はこちらを優先する。
        /// 有効な行が3行未満なら -1(呼び側で箱幅ベースにフォールバック)</summary>
        public static float RefineWidthPx(MsKeyframe kf, TrunkBox b)
        {
            int W = kf.Width, H = kf.Height;
            float bw = b.U1 - b.U0;
            var widths = new List<float>();
            float[] bandFracs = { 0.40f, 0.48f, 0.56f, 0.64f, 0.72f };
            foreach (float f in bandFracs)
            {
                int v = (int)(b.V0 + (b.V1 - b.V0) * f);
                if (v < 2 || v >= H - 2) continue;
                int lo = Mathf.Max(3, (int)(b.U0 - bw * 0.1f));
                int hi = Mathf.Min(W - 4, (int)(b.U1 + bw * 0.1f));
                int n = hi - lo;
                if (n < 8) continue;
                int cx = n / 2;
                int li = -1, ri = -1;
                float lg = 0, rg = 0;
                int row = v * W;
                for (int x = 2; x < n - 2; x++)
                {
                    // 3px平滑化した中心差分(±2px)
                    int ua = lo + x - 2, uc = lo + x + 2;
                    float a3 = (kf.Image[row + ua - 1] + kf.Image[row + ua] + kf.Image[row + ua + 1]) / 3f;
                    float c3 = (kf.Image[row + uc - 1] + kf.Image[row + uc] + kf.Image[row + uc + 1]) / 3f;
                    float g = Mathf.Abs(c3 - a3);
                    if (x < cx) { if (g > lg) { lg = g; li = x; } }
                    else { if (g > rg) { rg = g; ri = x; } }
                }
                if (lg < 12f || rg < 12f || li < 0 || ri < 0) continue;
                float wpx = ri - li;
                if (wpx >= 0.25f * bw && wpx <= 1.05f * bw) widths.Add(wpx);
            }
            if (widths.Count < 3) return -1f;
            widths.Sort();
            return widths[widths.Count / 2]; // 中央値(外れ行に頑健)
        }

        private static float IoU(TrunkBox a, TrunkBox b)
        {
            float x1 = Mathf.Max(a.U0, b.U0), y1 = Mathf.Max(a.V0, b.V0);
            float x2 = Mathf.Min(a.U1, b.U1), y2 = Mathf.Min(a.V1, b.V1);
            float inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
            float ua = (a.U1 - a.U0) * (a.V1 - a.V0) + (b.U1 - b.U0) * (b.V1 - b.V0) - inter;
            return ua > 0 ? inter / ua : 0;
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _worker = null;
        }
    }
}
