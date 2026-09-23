using System;
using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// 危険域の 1 種類ぶんの表示スタイル。
    ///
    /// 面で塗る領域を絞ることが最重要。光学シースルーでは半透明の塗りがそのまま
    /// 「視界にかかる膜」になるため、広い領域（立入禁止円など）を塗ると
    /// 森も足元も見えなくなり、安全装置が視界妨害に変わる。
    /// 広い領域は境界線として示し、面で塗るのは主落下方向だけにする。
    /// </summary>
    [Serializable]
    public class DangerRegionStyle
    {
        [Tooltip("UI に出す表示名。")]
        public string Label = "";

        [Tooltip("高彩度・高輝度で。光学シースルーでは暗色は沈んで見えない。")]
        public Color Color = Color.white;

        [Tooltip("面で塗るか。false なら輪郭線のみになる。広い領域ほど false にすること。")]
        public bool Filled = true;

        [Tooltip("塗りの不透明度。Filled が false のときは無視される。")]
        [Range(0f, 1f)] public float FillOpacity = 0.2f;

        [Tooltip("輪郭線の幅（m）。遠くの境界ほど太くしないと読めない。")]
        public float OutlineWidth = 0.25f;

        [Tooltip("ハッチングと明滅の強さ。危険度が高いものほど大きく。")]
        [Range(0f, 1f)] public float Severity = 1f;

        [Tooltip("境界に立てる垂直の壁の高さ（m）。0 で壁なし。\n" +
                 "地面の線は見る角度が浅いと潰れて読めなくなるが、垂直面は角度に依らず高さを持つ。\n" +
                 "上へ向かって透明になるので視界は塞がない。")]
        [Range(0f, 5f)] public float WallHeight = 0f;

        [Tooltip("壁の不透明度。輪郭のみの領域でも壁は見せたいので、塗りとは別に持つ。")]
        [Range(0f, 1f)] public float WallOpacity = 0.25f;
    }
}
