using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 作業中ページ (ARグラス連携モード) のController。
/// 開いた時点でXREAL Eyeカメラの映像取得 (EyeCameraService) を開始し、プレビューを表示する。
/// この映像が選木AI (Sentis) への入力パイプラインになる。
/// 「作業を終了する」→ ダイアログで「記録して続ける (計画は進行中のまま) / 記録して完了 / キャンセル」を選ぶ。
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

        // グラス側 (ArDemoController) が作業中の計画を参照できるようにIDを渡す
        ArDemoController.CurrentPlanId = plan.Id;

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

        // 作業中の視界画像を姿勢付きで定期保存する (この計画に紐づく。オンライン復帰後にまとめてアップロード)
        PlanCaptureService.Begin(router.gameObject, plan.Id);

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
            // 画像蓄積の状況 (枚数と容量の目安)
            var capture = PlanCaptureService.Active;
            if (capture != null && capture.SavedCount > 0)
            {
                diag += $" 撮影:{capture.SavedCount}枚 ({capture.SavedBytes / (1024 * 1024)}MB)";
            }
            // ライブ配信の視聴アドレスは短い独立行で出す (長い計測行に混ぜると画面端で見切れる)
            string live = ArDemoController.LiveViewUrl;
            statusLabel.text = (string.IsNullOrEmpty(measure)
                ? $"{cameraStatus}\n{diag}"
                : $"{cameraStatus}\n{diag}\n計測: {measure}")
                + (string.IsNullOrEmpty(live) ? "" : $"\n配信: {live}");
        }).Every(100);

        // 実施中の計画の概要
        var summary = page.Q<VisualElement>("plan-summary");
        AddRow(summary, "実施日", $"{plan.Date.Month}/{plan.Date.Day}");
        AddRow(summary, "樹種", plan.Species ?? "-");
        AddRow(summary, "間伐率", $"{plan.ThinningRatePercent}%");
        AddRow(summary, "しきい値 (伐採間隔)", $"{plan.FellingIntervalM:0.0}m");
        AddRow(summary, "対象面積", $"{plan.AreaHa:0.0#}ha");

        // ARグラス操作の代替ボタン (音声「きじゅん」「マーク」「かかりぎ」が使えないときの手元操作。結果はグラスのHUDに出る)
        page.Q<Button>("anchor-button").clicked += () => ArDemoController.SetAnchorHere();
        page.Q<Button>("mark-button").clicked += () => ArDemoController.MarkTreeAtGaze();
        // かかり木は段階に合わせて1つのボタンで進める (3点を順にマーク):
        //   「かかり木」(根元) → 「支持木」(相手の木の根元) → 「接点」(触れている所→計算) → 「かかり木解除」
        // 途中でやめたいときは音声「かいじょ」
        var kakarigiButton = page.Q<Button>("kakarigi-button");
        kakarigiButton.clicked += () =>
        {
            switch (ArDemoController.KakarigiState)
            {
                case KakarigiDisplay.State.Idle: ArDemoController.MarkKakarigiAtGaze(); break;
                case KakarigiDisplay.State.TargetMarked: ArDemoController.MarkSupportAtGaze(); break;
                case KakarigiDisplay.State.SupportMarked: ArDemoController.MarkContactAtGaze(); break;
                default: ArDemoController.ClearKakarigi(); break;
            }
        };
        page.schedule.Execute(() =>
        {
            switch (ArDemoController.KakarigiState)
            {
                case KakarigiDisplay.State.Idle: kakarigiButton.text = "かかり木"; break;
                case KakarigiDisplay.State.TargetMarked: kakarigiButton.text = "支持木"; break;
                case KakarigiDisplay.State.SupportMarked: kakarigiButton.text = "接点"; break;
                default: kakarigiButton.text = "かかり木解除"; break;
            }
        }).Every(300);

        // 誤タップ防止のためダイアログを挟む (記録して続ける / 記録して完了 / キャンセルの3択)
        page.Q<Button>("finish-button").clicked += ShowFinishDialog;
        page.Q<Button>("finish-cancel-button").clicked += HideFinishDialog;
        page.Q<Button>("finish-continue-button").clicked += () => CompleteWork(completePlan: false);
        page.Q<Button>("finish-complete-button").clicked += () => CompleteWork(completePlan: true);
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
    /// 作業終了: 計画に紐づけて作業記録を保存する (計画は削除しない。計画vs実績の比較用)。
    /// completePlan=false なら計画は進行中のまま予定に残り、後日同じ計画で作業を続けられる (計画の使い回し)。
    /// completePlan=true なら計画を完了にして予定一覧から外す。
    /// </summary>
    void CompleteWork(bool completePlan)
    {
        // 撮影を先に閉じる (メタファイルを確定させてからアップロード対象にする)
        PlanCaptureService.Active?.End();
        _eyeCamera.StopCapture();
        ArDemoController.StopDemo();
        ArDemoController.CurrentPlanId = null; // 作業を抜けるので計画IDを外す

        RecordStore.Add(new WorkRecord
        {
            PlanId = _plan.Id,
            Date = DateTime.Today,
            AreaHa = _plan.AreaHa,
            ThinningRatePercent = _plan.ThinningRatePercent,
            RangePoints = new List<Vector2>(_plan.RangePoints ?? new List<Vector2>()),
        });

        if (completePlan)
        {
            PlanStore.Complete(_plan);
        }

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
