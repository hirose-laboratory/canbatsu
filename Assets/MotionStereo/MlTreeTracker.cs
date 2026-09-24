using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanbatsuMS
{
    /// <summary>
    /// ML幹検出(TrunkDetectorML)の箱と三角測量点群を突き合わせて木の実座標を求め、
    /// 「2回以上の計測ペアで確認できた木」だけを返す追跡器。表示には依存しない純ロジック。
    ///
    /// 役割分担: 「木かどうか」= ML(見た目) / 「どこにあるか・何m」= モーションステレオの点群。
    /// MLの箱の中にある点だけを最前面クラスタ化して位置を決めるので、
    /// 幾何方式(縦長クラスタ=木)の誤検出(斜面の地形・人工物)が原理的に出ない。
    ///
    /// 使い方(ArDemoController の計測結果ハンドラ内を想定):
    ///   _tracker ??= new MlTreeTracker { Detector = TrunkDetectorML.TryCreate() };
    ///   var mlTrees = _tracker.Track(r, motionStereo.LastKeyframeA, sessionScale, groundY, camPosReal);
    ///   if (mlTrees != null) 表示に使う; else TreeDetectorMS の結果へフォールバック;
    ///   リセット時は _tracker.Reset();
    ///
    /// 実機実績(Beam Pro, 2026-09-15/16): 推論約0.65秒/回(GPUCompute)、
    /// 幽霊(単発誤検出)抑止・同一木の2重登録統合・0.6m以上の実ペア保護を屋外で確認済み。
    /// </summary>
    public class MlTreeTracker
    {
        /// <summary>ML検出器。nullのとき Track() はnullを返す(呼び側でフォールバック)</summary>
        public TrunkDetectorML Detector;

        /// <summary>曲がり木分類 (任意)。nullなら曲がり判定なし (BentScoreは-1のまま)</summary>
        public BendClassifierML BendClassifier;

        /// <summary>この回数以上のペアで確認できた木だけを返す(幽霊対策)</summary>
        public int MinSeenCount = 2;
        /// <summary>基準位置からこの距離[m]を超えた木は蓄積から破棄</summary>
        public float MaxRangeMeters = 10f;
        /// <summary>同一フレーム内の2箱をまとめる距離[m](ごく近距離のみ。併木を守るため小さく)</summary>
        public float FrameDedupMeters = 0.35f;
        /// <summary>フレーム間で同じ木とみなす距離[m](ドリフト許容。0.6m以上の実ペアは合体しない)</summary>
        public float MergeMeters = 0.55f;
        /// <summary>位置更新で収れんした2エントリを統合する距離[m]</summary>
        public float ConsolidateMeters = 0.45f;
        /// <summary>1本の木に最低限必要な三角測量点数</summary>
        public int MinPointsPerTree = 3;

        private readonly List<MsTree> _trees = new List<MsTree>();

        /// <summary>直近の Track() の推論時間 [ms](診断表示用)</summary>
        public float LastInferenceMs { get; private set; }

        /// <summary>蓄積とマーカーをやり直すときに呼ぶ(計測リセットと同時に)</summary>
        public void Reset()
        {
            _trees.Clear();
        }

        /// <summary>
        /// 1回の計測結果を取り込み、確認済みの木リストを返す。
        /// ML が使えない/失敗した場合は null(呼び側で TreeDetectorMS へフォールバック)。
        /// </summary>
        /// <param name="r">モーションステレオの計測結果(Points の U/V/WorldPosition を使う)</param>
        /// <param name="kf">キーフレームA(MotionStereoController.LastKeyframeA)</param>
        /// <param name="sessionScale">実寸 = SLAM座標 × このスケール(較正なしなら1)</param>
        /// <param name="groundY">地面の高さ(実寸系)。TreeDetectorMS の推定値を渡す</param>
        /// <param name="camPosReal">現在のカメラ位置(実寸系 = SLAM位置 × sessionScale)</param>
        public List<MsTree> Track(MsResult r, MsKeyframe kf, float sessionScale,
                                  float groundY, Vector3 camPosReal)
        {
            if (Detector == null || r == null || kf == null) return null;

            List<TrunkBox> boxes;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                boxes = Detector.Detect(kf);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrunkML] 推論失敗: " + e.Message);
                return null;
            }
            LastInferenceMs = (Time.realtimeSinceStartup - t0) * 1000f;

            Vector3 camA = kf.CamPosition * sessionScale;
            int made = 0;
            var frameTrees = new List<MsTree>();
            foreach (var b in boxes)
            {
                // 箱の中の三角測量点を集める
                var hits = new List<Vector3>();
                foreach (var p in r.Points)
                    if (p.U >= b.U0 && p.U <= b.U1 && p.V >= b.V0 && p.V <= b.V1)
                        hits.Add(p.WorldPosition * sessionScale);
                if (hits.Count < MinPointsPerTree) continue; // 位置を決められない箱はスキップ

                // 最前面クラスタ(奥の別の木・背景の点を距離で分離)
                hits.Sort((a2, b2) => (a2 - camA).sqrMagnitude.CompareTo((b2 - camA).sqrMagnitude));
                var cluster = new List<Vector3> { hits[0] };
                float prev = (hits[0] - camA).magnitude;
                for (int i = 1; i < hits.Count; i++)
                {
                    float d = (hits[i] - camA).magnitude;
                    if (d - prev > 0.8f)
                    {
                        if (cluster.Count >= MinPointsPerTree) break;
                        cluster.Clear();
                    }
                    cluster.Add(hits[i]);
                    prev = d;
                }
                if (cluster.Count < MinPointsPerTree) continue;

                float mx = 0, mz = 0, md = 0;
                foreach (var p in cluster) { mx += p.x; mz += p.z; md += (p - camA).magnitude; }
                mx /= cluster.Count; mz /= cluster.Count; md /= cluster.Count;

                // 幹の太さ: 箱内の輝度エッジで実測(箱は枝や傾きで広めに出るため)。
                // 取れない場合は箱幅×0.7の概算にフォールバック
                // 曲がり分類 (幹の箱を切り出して判定。細すぎる箱は-1=未判定)
                float bent = BendClassifier != null ? BendClassifier.Classify(kf, b) : -1f;

                float wpx = TrunkDetectorML.RefineWidthPx(kf, b);
                if (wpx <= 0) wpx = (b.U1 - b.U0) * 0.7f;
                var tree = new MsTree
                {
                    X = mx, Z = mz,
                    GroundY = groundY,
                    PointCount = cluster.Count,
                    WidthMeters = Mathf.Clamp(wpx / kf.Fx * md, 0.08f, 0.8f),
                    YExtent = (b.V1 - b.V0) / kf.Fy * md,
                    BentScore = bent,
                };

                // 同一フレーム内の重複(1本の木に複数の箱)は点数の多い方に統合。
                // しきい値は小さく=同一フレームで離れて写る2箱は別の木(併木)として残す
                // 曲がりスコアは「どこかの箱で曲がって見えた」を残したいので最大値を引き継ぐ
                bool dupInFrame = false;
                for (int i = 0; i < frameTrees.Count; i++)
                {
                    float fdx = frameTrees[i].X - mx, fdz = frameTrees[i].Z - mz;
                    if (fdx * fdx + fdz * fdz < FrameDedupMeters * FrameDedupMeters)
                    {
                        float maxBent = Mathf.Max(tree.BentScore, frameTrees[i].BentScore);
                        if (tree.PointCount > frameTrees[i].PointCount) frameTrees[i] = tree;
                        frameTrees[i].BentScore = maxBent;
                        dupInFrame = true;
                        break;
                    }
                }
                if (!dupInFrame) frameTrees.Add(tree);
                made++;
            }

            // 蓄積: ドリフト許容内で「最近傍」の既存エントリに統合し、確認回数を加算
            foreach (var tree in frameTrees)
            {
                int best = -1;
                float bestD2 = MergeMeters * MergeMeters;
                for (int i = 0; i < _trees.Count; i++)
                {
                    float dx = _trees[i].X - tree.X, dz = _trees[i].Z - tree.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best >= 0)
                {
                    tree.SeenCount = _trees[best].SeenCount + 1;
                    // 曲がりは角度によって見えたり見えなかったりするので最大値を保持する
                    tree.BentScore = Mathf.Max(tree.BentScore, _trees[best].BentScore);
                    _trees[best] = tree;
                }
                else
                {
                    tree.SeenCount = 1;
                    _trees.Add(tree);
                }
            }

            // 位置更新で収れんした2エントリ(同じ木の2重登録)を統合
            for (int i = 0; i < _trees.Count; i++)
            {
                for (int j = _trees.Count - 1; j > i; j--)
                {
                    float ddx = _trees[i].X - _trees[j].X, ddz = _trees[i].Z - _trees[j].Z;
                    if (ddx * ddx + ddz * ddz < ConsolidateMeters * ConsolidateMeters)
                    {
                        int seen = _trees[i].SeenCount + _trees[j].SeenCount;
                        float maxBent = Mathf.Max(_trees[i].BentScore, _trees[j].BentScore);
                        if (_trees[j].PointCount > _trees[i].PointCount)
                            _trees[i] = _trees[j];
                        _trees[i].SeenCount = seen;
                        _trees[i].BentScore = maxBent;
                        _trees.RemoveAt(j);
                    }
                }
            }

            // 遠すぎる木を掃除(古いドリフトした木も自然に消える)
            _trees.RemoveAll(t => t.HorizontalDistanceFrom(camPosReal) > MaxRangeMeters);

            // 確認済みの木だけを返す(確定0本なら空リスト=そのまま「0本」を表示してよい)
            var confirmed = new List<MsTree>();
            foreach (var t in _trees)
                if (t.SeenCount >= MinSeenCount) confirmed.Add(t);
            Debug.Log($"[TrunkML] 箱{boxes.Count} 採用{made} 累計{_trees.Count}本(確定{confirmed.Count}) ({LastInferenceMs:F0}ms)");
            return confirmed;
        }
    }
}
