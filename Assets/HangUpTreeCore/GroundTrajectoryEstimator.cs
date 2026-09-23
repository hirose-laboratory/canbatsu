using System;
using System.Collections.Generic;
using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// 装着者が歩いた軌跡から地面を推定する。
    ///
    /// XREAL One シリーズは平面検出も深度メッシュも持たない（対応表で確認済み。
    /// これらは Air 2 Ultra 専用）。使えるのは 6DoF の頭位置だけなので、
    /// 「装着者は地面の上に立っている」という事実を使って地面を逆算する。
    ///
    /// 頭の軌跡は、目線の高さぶん上を平行移動した地面そのものになる。
    /// 数歩ぶんの点に平面を当てはめれば、斜面でも傾きと谷方向が出る。
    /// 高さの仮定だけに頼る方式と違い、斜面で破綻しないのが利点。
    ///
    /// 限界も明確で、装着者が歩いた範囲の地面しか分からない。
    /// 遠くの起伏は推定に入らないので、単一平面近似であることは変わらない。
    /// </summary>
    [Serializable]
    public sealed class GroundTrajectoryEstimator
    {
        /// <summary>この距離以上動いたときだけ記録する。停止中の微動でバッファを埋めないため。</summary>
        public float MinSampleSpacing = 0.25f;

        /// <summary>
        /// 保持する最大サンプル数。
        ///
        /// 古い順に捨てるため、上限が小さいと「直近に歩いた細長い部分」だけが残り、
        /// 面的に歩いたはずなのに直線性の判定で棄却される。
        /// 広く歩き回る前提なので、余裕を持たせてある。
        /// </summary>
        public int MaxSamples = 256;

        /// <summary>この広がり（m）が無いと平面を当てはめない。一直線や一点では傾きが決まらない。</summary>
        public float MinSpread = 1.2f;

        /// <summary>この秒数を超えた古いサンプルは捨てる。移動して地面が変わった場合に追従するため。</summary>
        public float MaxSampleAge = 90f;

        /// <summary>地面法線が鉛直からこれ以上傾いていたら、推定が壊れたとみなす。</summary>
        public float MaxSlopeDeg = 60f;

        /// <summary>
        /// この角度未満の傾きは水平として扱う（不感帯）。
        ///
        /// 歩行の上下動とトラッキングのドリフトで、平らな場所でも数度の傾きが出る。
        /// 実測では平坦な屋内で 7° 程度が残った。これをそのまま採用すると、
        /// 平地なのに危険域が谷側へ伸びてしまう。
        /// 緩斜面を水平と誤るより、平地を斜面と誤る方が実害が大きいのでこちらを切る。
        /// </summary>
        public float FlatThresholdDeg = 8f;

        /// <summary>
        /// 法線の平滑化係数（0〜1）。1 で平滑化なし。
        /// 歩くたびに危険域の向きが変わると読めないので、時間方向に均す。
        /// </summary>
        public float NormalSmoothing = 0.15f;

        /// <summary>
        /// サンプルが細長く並んでいるときは棄却する比率のしきい値。
        /// 直線的に往復しただけでは、その直線まわりの回転が決まらず傾きが暴れる。
        ///
        /// 現場の歩行が整った正方形になることはないので、厳しくしすぎると常に棄却される。
        /// 一直線（比 ≒ 0）を弾ければ目的は足りる。
        /// </summary>
        public float MinPlanarRatio = 0.15f;

        private Vector3 _smoothedNormal = Vector3.up;
        private bool _hasSmoothed;

        private readonly List<Vector3> _points = new List<Vector3>();
        private readonly List<float> _times = new List<float>();

        public int SampleCount => _points.Count;

        /// <summary>サンプルの広がり（バウンディングボックスの対角長）。</summary>
        public float Spread { get; private set; }

        /// <summary>
        /// 足元の位置を記録する。頭の位置から目線の高さを引いた点を渡すこと。
        /// </summary>
        public void AddSample(Vector3 footPoint, float time)
        {
            PruneOld(time);

            if (_points.Count > 0)
            {
                Vector3 last = _points[_points.Count - 1];
                if (Vector3.Distance(last, footPoint) < MinSampleSpacing) return;
            }

            _points.Add(footPoint);
            _times.Add(time);

            while (_points.Count > MaxSamples)
            {
                _points.RemoveAt(0);
                _times.RemoveAt(0);
            }

            Spread = ComputeSpread();
        }

        /// <summary>
        /// 記録した点に平面を当てはめる。3 点以上かつ十分な広がりが要る。
        /// </summary>
        public bool TryFit(out Plane plane, out Vector3 origin)
        {
            plane = default;
            origin = default;

            if (_points.Count < 3 || Spread < MinSpread) return false;

            Vector3 centroid = Vector3.zero;
            foreach (Vector3 p in _points) centroid += p;
            centroid /= _points.Count;

            // 共分散行列の各成分
            float xx = 0f, xy = 0f, xz = 0f, yy = 0f, yz = 0f, zz = 0f;
            foreach (Vector3 p in _points)
            {
                Vector3 r = p - centroid;
                xx += r.x * r.x; xy += r.x * r.y; xz += r.x * r.z;
                yy += r.y * r.y; yz += r.y * r.z; zz += r.z * r.z;
            }

            // 最小固有ベクトル＝法線。行列式が最大の軸を使うのが数値的に安定する。
            float detX = yy * zz - yz * yz;
            float detY = xx * zz - xz * xz;
            float detZ = xx * yy - xy * xy;
            float detMax = Mathf.Max(detX, Mathf.Max(detY, detZ));

            if (detMax <= 1e-8f) return false; // 一直線に並んでいて平面が決まらない

            Vector3 normal;
            if (Mathf.Approximately(detMax, detX))
                normal = new Vector3(detX, xz * yz - xy * zz, xy * yz - xz * yy);
            else if (Mathf.Approximately(detMax, detY))
                normal = new Vector3(xz * yz - xy * zz, detY, xy * xz - yz * xx);
            else
                normal = new Vector3(xy * yz - xz * yy, xy * xz - yz * xx, detZ);

            if (normal.sqrMagnitude < 1e-8f) return false;

            normal.Normalize();
            if (Vector3.Dot(normal, Vector3.up) < 0f) normal = -normal; // 上向きに揃える

            // 歩行の揺れやトラッキングのずれで法線が暴れることがある。
            // 明らかにおかしい傾きは採用しない（過小警告より安全側）。
            if (Vector3.Angle(normal, Vector3.up) > MaxSlopeDeg) return false;

            // 点が細長く並んでいると、その直線まわりの回転が決まらず傾きが暴れる。
            // 実測でも、往復しただけのときに 12°→33° と発散した。
            if (PlanarRatio(centroid) < MinPlanarRatio) return false;

            // 平坦でも数度の傾きが残るので、小さい傾きは水平に丸める。
            if (Vector3.Angle(normal, Vector3.up) < FlatThresholdDeg) normal = Vector3.up;

            // 歩くたびに向きが変わると読めないので、時間方向に均す。
            if (!_hasSmoothed)
            {
                _smoothedNormal = normal;
                _hasSmoothed = true;
            }
            else
            {
                _smoothedNormal = Vector3.Slerp(_smoothedNormal, normal, Mathf.Clamp01(NormalSmoothing));
            }

            plane = new Plane(_smoothedNormal, centroid);
            origin = centroid;
            return true;
        }

        /// <summary>
        /// 水平面内での分布の「太さ」。1 に近いほど面的、0 に近いほど直線的。
        ///
        /// 直線的に往復しただけの軌跡は、その直線まわりの回転が決まらないため
        /// 平面の傾きが定まらない。対角長（Spread）だけでは細長い分布を見抜けないので、
        /// 主成分の比で判定する。
        /// </summary>
        public float PlanarRatio(Vector3 centroid)
        {
            if (_points.Count < 3) return 0f;

            // 水平成分の 2x2 共分散から、長軸と短軸の比を出す
            float xx = 0f, xz = 0f, zz = 0f;
            foreach (Vector3 p in _points)
            {
                float dx = p.x - centroid.x;
                float dz = p.z - centroid.z;
                xx += dx * dx; xz += dx * dz; zz += dz * dz;
            }

            float trace = xx + zz;
            float det = xx * zz - xz * xz;
            float disc = trace * trace * 0.25f - det;
            if (disc < 0f) disc = 0f;

            float root = Mathf.Sqrt(disc);
            float major = trace * 0.5f + root;
            float minor = trace * 0.5f - root;

            return major <= 1e-8f ? 0f : Mathf.Sqrt(Mathf.Max(0f, minor) / major);
        }

        public void Reset()
        {
            _points.Clear();
            _times.Clear();
            Spread = 0f;
            _hasSmoothed = false;
            _smoothedNormal = Vector3.up;
        }

        private void PruneOld(float now)
        {
            while (_times.Count > 0 && now - _times[0] > MaxSampleAge)
            {
                _times.RemoveAt(0);
                _points.RemoveAt(0);
            }
        }

        private float ComputeSpread()
        {
            if (_points.Count < 2) return 0f;

            Vector3 min = _points[0];
            Vector3 max = _points[0];
            foreach (Vector3 p in _points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (max - min).magnitude;
        }
    }
}
