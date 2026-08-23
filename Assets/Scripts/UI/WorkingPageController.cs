using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 作業中ページ (ARグラス連携モード) のController。
/// 開いた時点でXREAL Eyeカメラの映像取得 (EyeCameraService) を開始し、プレビューを表示する。
/// この映像が選木AI (Sentis) への入力パイプラインになる。
/// 「作業を終了する」→ 確認ダイアログ → カメラ停止 → 計画を作業記録に変えてホームへ戻る。
/// </summary>
public class WorkingPageController
{
    readonly DateTime _startTime = DateTime.Now;
    readonly AppRouter _router;
    readonly WorkPlan _plan;
    readonly VisualElement _finishDialog;
    readonly EyeCameraService _eyeCamera;

    public WorkingPageController(VisualElement page, AppRouter router, WorkPlan plan)
    {
        _router = router;
        _plan = plan;
        _finishDialog = page.Q<VisualElement>("finish-dialog");

        // 先にグラス側のAR (XRセッション+デモ表示) を起動し、その後カメラを開く
        // (カメラはXR起動後でないと開けないため。スマホ画面はこのページのまま)
        try
        {
            ArDemoController.StartDemo();
        }
        catch (Exception e)
        {
            Debug.LogException(e); // ARが起動できなくても作業中ページとカメラは動かす
        }

        // XREAL Eyeカメラの映像取得を開始 (エディタではPCカメラで代用)
        _eyeCamera = router.GetComponent<EyeCameraService>();
        if (_eyeCamera == null) _eyeCamera = router.gameObject.AddComponent<EyeCameraService>();
        _eyeCamera.StartCapture();

        // プレビュー表示 (UI ToolkitのImageにテクスチャを流し込み、定期的に描き直す)
        var previewImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        previewImage.style.flexGrow = 1;
        page.Q<VisualElement>("eye-preview").Add(previewImage);
        var statusLabel = page.Q<Label>("eye-status");
        page.schedule.Execute(() =>
        {
            previewImage.image = _eyeCamera.PreviewTexture;
            previewImage.MarkDirtyRepaint();
            string cameraStatus = _eyeCamera.Resolution.x > 0
                ? $"{_eyeCamera.Status} ({_eyeCamera.Resolution.x}x{_eyeCamera.Resolution.y})"
                : _eyeCamera.Status;
            // 実機検証用の診断表示 (XRが動いているか / 画面サイズ / 描画経路)。安定したら消す
            string diag =
                $"XR:{(XrSession.IsRunning ? "起動" : "停止")} " +
                $"表示:{(XrSession.IsDisplayRunning() ? "動作" : "なし")} " +
                $"画面:{Screen.width}x{Screen.height} " +
                $"UI:{(PhoneScreenUi.IsAttached ? "テクスチャ" : "通常")} " +
                $"カメラ数:{Camera.allCamerasCount}";
            // モーションステレオ (距離計測) の状態。計測プロトコル: 5m歩く→静止→横30cmステップ→静止
            string measure = ArDemoController.MeasurementStatus;
            statusLabel.text = string.IsNullOrEmpty(measure)
                ? $"{cameraStatus}\n{diag}"
                : $"{cameraStatus}\n{diag}\n計測: {measure}";
        }).Every(100);

        // 実施中の計画の概要
        var summary = page.Q<VisualElement>("plan-summary");
        AddRow(summary, "実施日", $"{plan.Date.Month}/{plan.Date.Day}");
        AddRow(summary, "樹種", plan.Species ?? "-");
        AddRow(summary, "間伐率", $"{plan.ThinningRatePercent}%");
        AddRow(summary, "しきい値 (伐採間隔)", $"{plan.FellingIntervalM:0.0}m");
        AddRow(summary, "対象面積", $"{plan.AreaHa:0.0#}ha");

        // 誤タップ防止のため確認ダイアログを挟む
        page.Q<Button>("finish-button").clicked += ShowFinishDialog;
        page.Q<Button>("finish-cancel-button").clicked += HideFinishDialog;
        page.Q<Button>("finish-confirm-button").clicked += CompleteWork;
        _finishDialog.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _finishDialog) HideFinishDialog();
        });

        // パルスアニメーション (緑の輪がゆっくり広がって薄くなる)
        var ring = page.Q<VisualElement>("pulse-ring");
        page.schedule.Execute(() =>
        {
            float phase = (float)((DateTime.Now - _startTime).TotalSeconds % 1.6) / 1.6f;
            float scale = 1f + phase * 0.5f;
            ring.style.scale = new Scale(new Vector3(scale, scale, 1f));
            ring.style.opacity = 1f - phase;
        }).Every(33);
    }

    void ShowFinishDialog() => _finishDialog.style.display = DisplayStyle.Flex;

    void HideFinishDialog() => _finishDialog.style.display = DisplayStyle.None;

    /// <summary>
    /// 作業終了: 計画から作業記録を作って保存する。
    /// 計画自体は削除せずPlanIdで紐づけて残す (計画vs実績の比較用)。
    /// 記録が付いた計画は予定一覧に出なくなる (HomePageControllerで除外)。
    /// </summary>
    void CompleteWork()
    {
        _eyeCamera.StopCapture();
        ArDemoController.StopDemo();

        RecordStore.Add(new WorkRecord
        {
            PlanId = _plan.Id,
            Date = DateTime.Today,
            FelledCount = 0, // TODO: ARグラスの自動カウントを受け取る (バックエンド待ち)
            AreaHa = _plan.AreaHa,
            ThinningRatePercent = _plan.ThinningRatePercent,
            RangePoints = new List<Vector2>(_plan.RangePoints ?? new List<Vector2>()),
        });

        _router.NavigateToHome();
    }

    static void AddRow(VisualElement container, string key, string value)
    {
        var row = new VisualElement();
        row.AddToClassList("working__card-row");

        var keyLabel = new Label(key);
        keyLabel.AddToClassList("working__card-key");
        row.Add(keyLabel);

        var valueLabel = new Label(value);
        valueLabel.AddToClassList("working__card-value");
        row.Add(valueLabel);

        container.Add(row);
    }
}
