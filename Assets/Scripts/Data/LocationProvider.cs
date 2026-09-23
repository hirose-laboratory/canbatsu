using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// 端末GPSの現在地を提供する常駐コンポーネント。
/// 使用側は router.GetComponent&lt;LocationProvider&gt;() ?? AddComponent で付けて StartUpdates() を呼ぶ
/// (WorkingPageControllerのEyeCameraServiceと同じパターン)。
/// Androidでは位置情報の権限を要求してから測位を開始する。
/// 権限拒否・位置情報オフ・エディタでは HasFix=false のまま (呼び出し側がフォールバック表示する)。
/// </summary>
public class LocationProvider : MonoBehaviour
{
    /// <summary>現在地が一度でも取れたか。falseの間はLatitude/Longitudeを使わないこと</summary>
    public static bool HasFix { get; private set; }

    public static double Latitude { get; private set; }
    public static double Longitude { get; private set; }

    /// <summary>方位が取れているか (コンパスのセンサー値が届いているか)。エディタではfalseのまま</summary>
    public static bool HasHeading { get; private set; }

    /// <summary>真北からの方位 [度・時計回り]。選木マップの向き合わせに使う (森の中では±10〜30°程度ぶれる)</summary>
    public static float HeadingDeg { get; private set; }

    bool _wantUpdates;    // StartUpdatesが呼ばれたか (権限待ちの間もUpdateで開始を試し続ける)
    bool _serviceStarted; // Input.location.Start を呼んだか

    /// <summary>測位を開始する。多重呼び出しは無視 (複数ページから呼ばれてよい)</summary>
    public void StartUpdates()
    {
        if (_wantUpdates) return;
        _wantUpdates = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        // 権限が無ければ先に要求する。許可されたらUpdate側で測位を開始する
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Permission.RequestUserPermission(Permission.FineLocation);
            return;
        }
#endif
        StartLocationService();
    }

    void StartLocationService()
    {
        if (_serviceStarted) return;
        if (!Input.location.isEnabledByUser) return; // 端末の位置情報がオフ (エディタもここで止まる)

        // 精度10m・10m移動ごとに更新 (山中の作業用途には十分で電池消費を抑えられる)
        Input.location.Start(10f, 10f);
        // コンパスも起動 (基準点の向きを真北基準で記録し、選木結果を地図に載せるのに使う)
        Input.compass.enabled = true;
        _serviceStarted = true;
    }

    void Update()
    {
        if (!_wantUpdates) return;

        if (!_serviceStarted)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 権限ダイアログで許可されるのを待ってから開始する
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation)) return;
#endif
            StartLocationService();
            return;
        }

        var status = Input.location.status;
        if (status == LocationServiceStatus.Running)
        {
            var data = Input.location.lastData;
            Latitude = data.latitude;
            Longitude = data.longitude;
            HasFix = true;

            // コンパス (timestampが入っていればセンサー値が届いている)
            if (Input.compass.enabled && Input.compass.timestamp > 0)
            {
                HeadingDeg = Input.compass.trueHeading;
                HasHeading = true;
            }
        }
        else if (status == LocationServiceStatus.Failed)
        {
            HasFix = false;
        }
    }

    void OnDestroy()
    {
        if (_serviceStarted) Input.location.Stop();
    }
}
