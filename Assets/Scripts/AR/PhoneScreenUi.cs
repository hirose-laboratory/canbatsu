using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.UIElements;

/// <summary>
/// XR起動中にスマホ画面へUI Toolkitのアプリ画面を出すための橋渡し。
///
/// XRが動いている間、UI Toolkitの通常描画 (画面オーバーレイ) はスマホ画面に届かない。
/// そこでパネルをRenderTextureに描かせ、それをスマホ用カメラ (allowXRRendering=false) が
/// ScreenSpace-CameraのUGUIで全画面表示する (XREAL SDKの仮想コントローラUIと同じ経路)。
/// タッチ座標は SetScreenToPanelSpaceFunction でテクスチャ座標に変換するので操作もそのまま効く。
/// AR中だけAttachし、終了でDetachして通常描画に戻す。
/// </summary>
public static class PhoneScreenUi
{
    static PanelSettings _panel;
    static RenderTexture _renderTexture;
    static GameObject _canvasGo;
    static bool _attached;

    /// <summary>テクスチャ経由の表示に切り替え中か (診断表示用)</summary>
    public static bool IsAttached => _attached;

    public static void Attach(Camera phoneCamera)
    {
        if (_attached || phoneCamera == null) return;

        var document = Object.FindFirstObjectByType<UIDocument>();
        if (document == null || document.panelSettings == null)
        {
            Debug.LogWarning("PhoneScreenUi: UIDocumentが見つからないためスマホ画面UIを切り替えできません");
            return;
        }
        _panel = document.panelSettings;

        // UI Toolkitの描画先を画面サイズのテクスチャにする
        int width = Mathf.Max(1, Screen.width);
        int height = Mathf.Max(1, Screen.height);
        _renderTexture = new RenderTexture(width, height, 24) { name = "PhoneScreenUI" };
        _renderTexture.Create();
        _panel.targetTexture = _renderTexture;

        // タッチ座標 → パネル座標 (左上が原点・下向きY・テクスチャ解像度) の変換。
        // 引数の座標系が環境で曖昧なので、Unity公式サンプルと同じくポインタ位置を直接読む (左下原点・上向きY)
        _panel.SetScreenToPanelSpaceFunction(screen =>
        {
            Vector2 pointer = screen;
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
                pointer = Touchscreen.current.primaryTouch.position.ReadValue();
            else if (Pointer.current != null)
                pointer = Pointer.current.position.ReadValue();

            return new Vector2(
                pointer.x * _renderTexture.width / Screen.width,
                (Screen.height - pointer.y) * _renderTexture.height / Screen.height);
        });

        // スマホ用カメラが描くUGUIキャンバスに、そのテクスチャを全画面で貼る
        int uiLayer = LayerMask.NameToLayer("UI");
        _canvasGo = new GameObject("PhoneScreenCanvas") { layer = uiLayer };
        var canvas = _canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = phoneCamera;
        canvas.planeDistance = 1f;

        var imageGo = new GameObject("PanelImage") { layer = uiLayer };
        imageGo.transform.SetParent(_canvasGo.transform, false);
        var rect = imageGo.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var rawImage = imageGo.AddComponent<RawImage>();
        rawImage.texture = _renderTexture;
        rawImage.raycastTarget = false; // タッチはUI Toolkit側に届かせる

        _attached = true;
        Debug.Log($"PhoneScreenUi: スマホ画面UIをテクスチャ経由に切替 ({width}x{height})");
    }

    public static void Detach()
    {
        if (!_attached) return;
        _attached = false;

        if (_panel != null)
        {
            _panel.targetTexture = null;          // 通常のオーバーレイ描画に戻す
            _panel.SetScreenToPanelSpaceFunction(null);
        }
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            Object.Destroy(_renderTexture);
        }
        _canvasGo = null;
        _renderTexture = null;
        _panel = null;
    }
}
