using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>
    /// モーションステレオ距離推定器 (Python/portable_algo.py の忠実な移植)
    ///
    /// パイプライン:
    ///   Shi-Tomasiコーナー検出 → 幾何予測付きピラミッドLK追跡(往復チェック+NCC検証)
    ///   → レイ中点法三角測量 → 注目領域の最前面クラスタ = 対象距離
    ///
    /// 検証実績 (実物の木, XREAL One Pro + Eye):
    ///   5m実測・50cmステップ較正あり → 誤差 -0.4%
    ///
    /// 純粋な計算のみ(Unity APIは数学型だけ)なのでワーカースレッドで実行可能。
    /// </summary>
    public class MotionStereoEstimator
    {
        public MsConfig Config = new MsConfig();

        // 作業バッファ(1回のEstimate内で再利用)
        private float[][] _pyrA, _pyrB;
        private int[][] _pyrSize; // [level] = {w, h}
        private float[] _patchT, _patchI, _gradX, _gradY;

        /// <summary>
        /// 2枚のキーフレームから対象距離を推定する。
        /// </summary>
        /// <param name="a">キーフレームA(このカメラ位置からの距離を返す)</param>
        /// <param name="b">キーフレームB</param>
        /// <param name="targetPixel">A画像上の対象位置(null=画像中央)</param>
        /// <param name="trueBaselineMeters">実測基線長(m)。>0 で6DoFスケールを補正</param>
        public MsResult Estimate(MsKeyframe a, MsKeyframe b,
                                 Vector2? targetPixel = null,
                                 float trueBaselineMeters = -1f)
        {
            var sw = Stopwatch.StartNew();
            var res = new MsResult();

            res.BaselineMeters = Vector3.Distance(a.CamPosition, b.CamPosition);
            if (res.BaselineMeters < 0.05f)
            {
                res.Message = "基線長が短すぎます(5cm未満)。もっと横に移動してください";
                return res;
            }
            if (trueBaselineMeters > 0f)
                res.ScaleFactor = trueBaselineMeters / res.BaselineMeters;

            int n = Config.PatchRadius * 2 + 1;
            _patchT = new float[n * n];
            _patchI = new float[n * n];
            _gradX = new float[n * n];
            _gradY = new float[n * n];
            BuildPyramid(a.Image, a.Width, a.Height, out _pyrA, out _pyrSize);
            BuildPyramid(b.Image, b.Width, b.Height, out _pyrB, out _pyrSize);

            List<Vector2> corners = DetectCorners(a.Image, a.Width, a.Height);
            int tracked = 0;
            foreach (var c in corners)
            {
                if (!TrackBidirectional(a, b, c.x, c.y, out float ub, out float vb))
                    continue;
                tracked++;
                if (Triangulate(a, b, c.x, c.y, ub, vb, out float dist))
                {
                    dist *= res.ScaleFactor;
                    if (dist > Config.MinDistance && dist < Config.MaxDistance)
                        res.Points.Add(new MsDepthPoint { U = c.x, V = c.y, DistanceMeters = dist });
                }
            }

            if (res.Points.Count < 5)
            {
                res.Message = $"三角測量点が不足 (コーナー{corners.Count}/追跡{tracked}/点{res.Points.Count})。" +
                              "テクスチャのある対象で、向きを変えずに横移動してください";
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }

            // 注目領域内の最前面クラスタ
            float tu = targetPixel.HasValue ? targetPixel.Value.x : a.Cx;
            float tv = targetPixel.HasValue ? targetPixel.Value.y : a.Cy;
            float roiR = Config.RoiRadiusFraction * a.Width;
            var roiDepths = new List<float>();
            foreach (var p in res.Points)
            {
                float du = p.U - tu, dv = p.V - tv;
                if (du * du + dv * dv < roiR * roiR)
                    roiDepths.Add(p.DistanceMeters);
            }
            if (roiDepths.Count == 0)
            {
                res.Message = "注目領域に対応点がありません。対象を画面中央に入れてください";
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }

            ClusterDepths(roiDepths, res.RoiClusters, out float target, out int targetCount);
            res.TargetDistanceMeters = target;
            res.TargetPointCount = targetCount;
            res.Success = true;
            res.Message = $"OK ({targetCount}点)";
            res.ElapsedMs = sw.ElapsedMilliseconds;
            return res;
        }

        // ---------- コーナー検出 (Shi-Tomasi, セルごとに最良1点) ----------

        private List<Vector2> DetectCorners(float[] img, int w, int h)
        {
            var corners = new List<Vector2>();
            // 勾配とその積
            var ixx = new float[w * h];
            var iyy = new float[w * h];
            var ixy = new float[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    float gx = (img[row + x + 1] - img[row + x - 1]) * 0.5f;
                    float gy = (img[row + w + x] - img[row - w + x]) * 0.5f;
                    ixx[row + x] = gx * gx;
                    iyy[row + x] = gy * gy;
                    ixy[row + x] = gx * gy;
                }
            }
            int cell = Config.CornerCellSize, margin = Config.CornerMargin;
            for (int cy0 = margin; cy0 < h - margin; cy0 += cell)
            {
                for (int cx0 = margin; cx0 < w - margin; cx0 += cell)
                {
                    float bestScore = Config.CornerMinScore;
                    int bestX = -1, bestY = -1;
                    int yEnd = Math.Min(cy0 + cell, h - margin);
                    int xEnd = Math.Min(cx0 + cell, w - margin);
                    for (int y = cy0; y < yEnd; y++)
                    {
                        for (int x = cx0; x < xEnd; x++)
                        {
                            // 3x3ボックス和の構造テンソル
                            float sxx = 0, syy = 0, sxy = 0;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int r = (y + dy) * w + x;
                                sxx += ixx[r - 1] + ixx[r] + ixx[r + 1];
                                syy += iyy[r - 1] + iyy[r] + iyy[r + 1];
                                sxy += ixy[r - 1] + ixy[r] + ixy[r + 1];
                            }
                            float tr = sxx + syy;
                            float dif = sxx - syy;
                            float lamMin = (tr - Mathf.Sqrt(dif * dif + 4f * sxy * sxy)) * 0.5f;
                            if (lamMin > bestScore)
                            {
                                bestScore = lamMin; bestX = x; bestY = y;
                            }
                        }
                    }
                    if (bestX >= 0)
                        corners.Add(new Vector2(bestX, bestY));
                }
            }
            return corners;
        }

        // ---------- ピラミッド ----------

        private void BuildPyramid(float[] img, int w, int h, out float[][] pyr, out int[][] sizes)
        {
            int levels = Config.PyramidLevels;
            pyr = new float[levels][];
            sizes = new int[levels][];
            pyr[0] = img;
            sizes[0] = new[] { w, h };
            for (int lv = 1; lv < levels; lv++)
            {
                int pw = sizes[lv - 1][0], ph = sizes[lv - 1][1];
                int nw = pw / 2, nh = ph / 2;
                var dst = new float[nw * nh];
                var src = pyr[lv - 1];
                for (int y = 0; y < nh; y++)
                {
                    int r0 = (y * 2) * pw, r1 = (y * 2 + 1) * pw;
                    for (int x = 0; x < nw; x++)
                    {
                        int x2 = x * 2;
                        dst[y * nw + x] = (src[r0 + x2] + src[r0 + x2 + 1] +
                                           src[r1 + x2] + src[r1 + x2 + 1]) * 0.25f;
                    }
                }
                pyr[lv] = dst;
                sizes[lv] = new[] { nw, nh };
            }
        }

        // ---------- バイリニアパッチ ----------

        /// <summary>(cx,cy)中心 (2r+1)^2 パッチを out バッファへ。範囲外なら false</summary>
        private bool BilinearPatch(float[] img, int w, int h, float cx, float cy, float[] dst)
        {
            int r = Config.PatchRadius;
            float x0 = cx - r, y0 = cy - r;
            if (x0 < 0f || y0 < 0f || cx + r >= w - 1 || cy + r >= h - 1)
                return false;
            int xi = (int)Mathf.Floor(x0), yi = (int)Mathf.Floor(y0);
            float ax = x0 - xi, ay = y0 - yi;
            float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay);
            float w01 = (1 - ax) * ay, w11 = ax * ay;
            int nSize = 2 * r + 1;
            for (int y = 0; y < nSize; y++)
            {
                int rowT = (yi + y) * w + xi;
                int rowB = rowT + w;
                int dRow = y * nSize;
                for (int x = 0; x < nSize; x++)
                {
                    dst[dRow + x] = w00 * img[rowT + x] + w10 * img[rowT + x + 1] +
                                    w01 * img[rowB + x] + w11 * img[rowB + x + 1];
                }
            }
            return true;
        }

        // ---------- ピラミッド LK 追跡 ----------

        /// <summary>点(u,v)@srcPyr を dstPyr 上へ追跡。initU/V は初期推定</summary>
        private bool LkTrack(float[][] srcPyr, float[][] dstPyr, int[][] sizes,
                             float u, float v, float initU, float initV,
                             out float outU, out float outV)
        {
            int levels = Config.PyramidLevels;
            int nSize = 2 * Config.PatchRadius + 1;
            float topScale = 1 << (levels - 1);
            float gu = initU / topScale, gv = initV / topScale;
            outU = 0; outV = 0;

            for (int lv = levels - 1; lv >= 0; lv--)
            {
                float scale = 1 << lv;
                float ul = u / scale, vl = v / scale;
                int w = sizes[lv][0], h = sizes[lv][1];
                if (!BilinearPatch(srcPyr[lv], w, h, ul, vl, _patchT))
                    return false;
                // テンプレート勾配
                float gxx = 0, gyy = 0, gxy = 0;
                for (int y = 0; y < nSize; y++)
                {
                    for (int x = 0; x < nSize; x++)
                    {
                        int i = y * nSize + x;
                        float gx = (x > 0 && x < nSize - 1)
                            ? (_patchT[i + 1] - _patchT[i - 1]) * 0.5f : 0f;
                        float gy = (y > 0 && y < nSize - 1)
                            ? (_patchT[i + nSize] - _patchT[i - nSize]) * 0.5f : 0f;
                        _gradX[i] = gx; _gradY[i] = gy;
                        gxx += gx * gx; gyy += gy * gy; gxy += gx * gy;
                    }
                }
                float det = gxx * gyy - gxy * gxy;
                if (det < 1e-6f)
                    return false;
                for (int it = 0; it < Config.LkIterations; it++)
                {
                    if (!BilinearPatch(dstPyr[lv], w, h, gu, gv, _patchI))
                        return false;
                    float bx = 0, by = 0;
                    for (int i = 0; i < nSize * nSize; i++)
                    {
                        float diff = _patchT[i] - _patchI[i];
                        bx += diff * _gradX[i];
                        by += diff * _gradY[i];
                    }
                    float du = (gyy * bx - gxy * by) / det;
                    float dv = (gxx * by - gxy * bx) / det;
                    gu += du; gv += dv;
                    if (du * du + dv * dv < 1e-4f)
                        break;
                }
                if (lv > 0) { gu *= 2f; gv *= 2f; }
            }
            outU = gu; outV = gv;
            return true;
        }

        /// <summary>幾何予測付き複数初期化 + 往復チェック + NCC検証</summary>
        private bool TrackBidirectional(MsKeyframe a, MsKeyframe b, float u, float v,
                                        out float outU, out float outV)
        {
            outU = 0; outV = 0;
            float bestErr = float.MaxValue;
            foreach (float z in Config.InitDepthHypotheses)
            {
                if (!ProjectToB(a, b, u, v, z, out float iu, out float iv))
                    continue;
                if (!LkTrack(_pyrA, _pyrB, _pyrSize, u, v, iu, iv, out float fu, out float fv))
                    continue;
                // 逆追跡は元コーナー位置を初期値に(大視差対応)
                if (!LkTrack(_pyrB, _pyrA, _pyrSize, fu, fv, u, v, out float bu, out float bv))
                    continue;
                float e = (bu - u) * (bu - u) + (bv - v) * (bv - v);
                if (e <= Config.FwdBwdMaxPixels * Config.FwdBwdMaxPixels && e < bestErr)
                {
                    bestErr = e; outU = fu; outV = fv;
                }
            }
            if (bestErr == float.MaxValue)
                return false;
            // 最終NCC検証
            int w0 = _pyrSize[0][0], h0 = _pyrSize[0][1];
            if (!BilinearPatch(_pyrA[0], w0, h0, u, v, _patchT)) return false;
            if (!BilinearPatch(_pyrB[0], w0, h0, outU, outV, _patchI)) return false;
            return Ncc(_patchT, _patchI) >= Config.MinNcc;
        }

        private static float Ncc(float[] a, float[] b)
        {
            int n = a.Length;
            float ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            float sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < n; i++)
            {
                float da = a[i] - ma, db = b[i] - mb;
                sab += da * db; saa += da * da; sbb += db * db;
            }
            float d = Mathf.Sqrt(saa * sbb);
            return d < 1e-6f ? -1f : sab / d;
        }

        // ---------- 幾何 ----------

        /// <summary>画素→ワールド方向レイ(カメラz=1で正規化された方向)</summary>
        private static Vector3 PixelRay(MsKeyframe f, float u, float v)
        {
            var d = new Vector3((u - f.Cx) / f.Fx, -(v - f.Cy) / f.Fy, 1f);
            return f.CamRotation * d;
        }

        /// <summary>A画素(u,v)を深度zと仮定してBへ投影</summary>
        private static bool ProjectToB(MsKeyframe a, MsKeyframe b, float u, float v, float z,
                                       out float ub, out float vb)
        {
            ub = 0; vb = 0;
            Vector3 X = a.CamPosition + PixelRay(a, u, v) * z;
            Vector3 xc = Quaternion.Inverse(b.CamRotation) * (X - b.CamPosition);
            if (xc.z < 0.2f)
                return false;
            ub = b.Cx + b.Fx * xc.x / xc.z;
            vb = b.Cy - b.Fy * xc.y / xc.z;
            return true;
        }

        /// <summary>レイ中点法三角測量 → カメラAからのユークリッド距離</summary>
        private bool Triangulate(MsKeyframe a, MsKeyframe b,
                                 float uA, float vA, float uB, float vB, out float dist)
        {
            dist = 0;
            Vector3 dA = PixelRay(a, uA, vA);
            Vector3 dB = PixelRay(b, uB, vB);
            Vector3 bl = b.CamPosition - a.CamPosition;
            // 連立方程式 [[dA·dA, -dA·dB], [dA·dB, -dB·dB]] [s,t] = [dA·bl, dB·bl]
            float m00 = Vector3.Dot(dA, dA), m01 = -Vector3.Dot(dA, dB);
            float m10 = Vector3.Dot(dA, dB), m11 = -Vector3.Dot(dB, dB);
            float r0 = Vector3.Dot(dA, bl), r1 = Vector3.Dot(dB, bl);
            float det = m00 * m11 - m01 * m10;
            if (Mathf.Abs(det) < 1e-9f)
                return false;
            float s = (r0 * m11 - m01 * r1) / det;
            float t = (m00 * r1 - r0 * m10) / det;
            if (s <= 0f || t <= 0f)
                return false;
            Vector3 xa = a.CamPosition + s * dA;
            Vector3 xb = b.CamPosition + t * dB;
            if (Vector3.Distance(xa, xb) > Config.MaxRaySkewMeters)
                return false;
            Vector3 X = (xa + xb) * 0.5f;
            // 前方チェック(カメラA前方成分)
            if (Vector3.Dot(X - a.CamPosition, a.CamRotation * Vector3.forward) <= 0f)
                return false;
            dist = Vector3.Distance(X, a.CamPosition);
            return true;
        }

        // ---------- クラスタリング ----------

        private void ClusterDepths(List<float> depths, List<MsCluster> outClusters,
                                   out float target, out int targetCount)
        {
            depths.Sort();
            var clusters = new List<List<float>>();
            var cur = new List<float> { depths[0] };
            for (int i = 1; i < depths.Count; i++)
            {
                if (depths[i] - cur[cur.Count - 1] > Config.ClusterGapMeters)
                {
                    clusters.Add(cur);
                    cur = new List<float>();
                }
                cur.Add(depths[i]);
            }
            clusters.Add(cur);

            int need = Math.Max(Config.ClusterMinPoints,
                                (int)(Config.ClusterMinFraction * depths.Count));
            List<float> best = null;
            List<float> largest = clusters[0];
            foreach (var c in clusters)
            {
                if (c.Count > largest.Count) largest = c;
                if (best == null && c.Count >= need) best = c; // 最前面(ソート済み)
                outClusters.Add(new MsCluster
                {
                    MedianMeters = Median(c),
                    PointCount = c.Count,
                    MinMeters = c[0],
                    MaxMeters = c[c.Count - 1],
                });
            }
            var t = best ?? largest;
            target = Median(t);
            targetCount = t.Count;
        }

        private static float Median(List<float> sorted)
        {
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5f;
        }
    }
}
