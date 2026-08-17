using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using UnityEngine.Windows.Speech;
#endif

/// <summary>
/// 音声コマンドの聞き取りサービス (ハンズフリー操作用)。
/// OS標準の音声認識を使う:
///  - Android実機: SpeechRecognizer (日本語。1回聞き取るたびに自動で聞き直しを繰り返す)
///  - Windowsエディタ: KeywordRecognizer (動作確認用。登録した単語だけ拾う)
/// 聞き取ったテキストを PhraseRecognized で通知するだけで、解釈 (どのページへ飛ぶか) はAppRouter側。
/// AppRouterが起動時に自動でAddComponentするのでシーンへの設定は不要。
/// TODO: 山での完全オフライン化が必要になったらVosk等に差し替える (AR企画書フェーズ6)
/// </summary>
public class VoiceCommandService : MonoBehaviour
{
    /// <summary>聞き取ったテキスト (メインスレッドで呼ばれる)</summary>
    public event Action<string> PhraseRecognized;

    /// <summary>状態やエラーの通知。トースト表示用 (メインスレッドで呼ばれる)</summary>
    public event Action<string> StatusChanged;

    public bool IsRunning { get; private set; }

    // 認識コールバックは別スレッドから来るので、キューに溜めてUpdateでメインスレッドに流す
    readonly object _lock = new object();
    readonly Queue<string> _pendingPhrases = new Queue<string>();
    readonly Queue<string> _pendingStatus = new Queue<string>();

    public void StartListening()
    {
        if (IsRunning) return;
        IsRunning = true;
        StartPlatform();
    }

    public void StopListening()
    {
        if (!IsRunning) return;
        IsRunning = false;
        StopPlatform();
    }

    void Update()
    {
        List<string> phrases = null;
        List<string> statuses = null;
        lock (_lock)
        {
            if (_pendingPhrases.Count > 0) { phrases = new List<string>(_pendingPhrases); _pendingPhrases.Clear(); }
            if (_pendingStatus.Count > 0) { statuses = new List<string>(_pendingStatus); _pendingStatus.Clear(); }
        }
        if (phrases != null) foreach (var p in phrases) PhraseRecognized?.Invoke(p);
        if (statuses != null) foreach (var s in statuses) StatusChanged?.Invoke(s);

        UpdatePlatform();
    }

    void OnDestroy() => StopListening();

    void EnqueuePhrase(string text)
    {
        lock (_lock) _pendingPhrases.Enqueue(text);
    }

    void EnqueueStatus(string text)
    {
        lock (_lock) _pendingStatus.Enqueue(text);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // ---- Android実機: OSのSpeechRecognizerを使う ----

    AndroidJavaObject _activity;
    AndroidJavaObject _recognizer;
    AndroidJavaObject _intent;
    volatile bool _restartRequested;
    float _restartAt = -1f;

    void StartPlatform()
    {
        // Unityビルドにマイク権限 (RECORD_AUDIO) を入れさせるためのダミー参照
        var _ = Microphone.devices;

        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
            IsRunning = false;
            EnqueueStatus("マイクを許可してから、もう一度オンにしてください");
            return;
        }

        _activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
            .GetStatic<AndroidJavaObject>("currentActivity");

        // SpeechRecognizerはAndroidのUIスレッドからしか操作できない
        _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
        {
            try
            {
                var recognizerClass = new AndroidJavaClass("android.speech.SpeechRecognizer");
                _recognizer = recognizerClass.CallStatic<AndroidJavaObject>("createSpeechRecognizer", _activity);
                _recognizer.Call("setRecognitionListener", new RecognitionListener(this));

                _intent = new AndroidJavaObject("android.content.Intent", "android.speech.action.RECOGNIZE_SPEECH");
                _intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE_MODEL", "free_form");
                _intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE", "ja-JP");

                _recognizer.Call("startListening", _intent);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"音声認識を開始できません: {e.Message}");
                EnqueueStatus("この端末では音声認識を開始できませんでした");
            }
        }));
    }

    void StopPlatform()
    {
        if (_activity == null || _recognizer == null) return;
        var recognizer = _recognizer;
        _recognizer = null;
        _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
        {
            try { recognizer.Call("destroy"); } catch (Exception) { }
        }));
    }

    void UpdatePlatform()
    {
        // 1回の聞き取りが終わる (結果かエラー) たびに、少し待ってから聞き直す
        if (_restartRequested)
        {
            _restartRequested = false;
            _restartAt = Time.unscaledTime + 0.4f;
        }
        if (_restartAt > 0 && Time.unscaledTime >= _restartAt)
        {
            _restartAt = -1f;
            if (!IsRunning || _activity == null || _recognizer == null) return;
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try { _recognizer.Call("startListening", _intent); } catch (Exception) { }
            }));
        }
    }

    void RequestRestart() => _restartRequested = true;

    /// <summary>AndroidのRecognitionListenerをC#で受けるためのプロキシ</summary>
    class RecognitionListener : AndroidJavaProxy
    {
        readonly VoiceCommandService _owner;

        public RecognitionListener(VoiceCommandService owner)
            : base("android.speech.RecognitionListener")
        {
            _owner = owner;
        }

        public void onResults(AndroidJavaObject results)
        {
            try
            {
                var list = results.Call<AndroidJavaObject>("getStringArrayList", "results_recognition");
                if (list != null && list.Call<int>("size") > 0)
                {
                    _owner.EnqueuePhrase(list.Call<string>("get", 0));
                }
            }
            catch (Exception) { }
            _owner.RequestRestart();
        }

        public void onError(int error) => _owner.RequestRestart();

        // 使わないが実装が必要なコールバック
        public void onReadyForSpeech(AndroidJavaObject @params) { }
        public void onBeginningOfSpeech() { }
        public void onRmsChanged(float rmsdB) { }
        public void onBufferReceived(byte[] buffer) { }
        public void onEndOfSpeech() { }
        public void onPartialResults(AndroidJavaObject partialResults) { }
        public void onEvent(int eventType, AndroidJavaObject @params) { }
    }

#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    // ---- Windowsエディタ: 動作確認用のKeywordRecognizer ----
    // Windowsに日本語の音声認識が入っていないと開始に失敗する (その場合は実機で確認)

    static readonly string[] Keywords = { "きろく", "記録", "よてい", "予定", "計画", "けいかく", "ホーム" };

    KeywordRecognizer _keywordRecognizer;

    void StartPlatform()
    {
        try
        {
            _keywordRecognizer = new KeywordRecognizer(Keywords);
            _keywordRecognizer.OnPhraseRecognized += OnKeyword;
            _keywordRecognizer.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"エディタの音声認識を開始できません: {e.Message}");
            IsRunning = false;
            EnqueueStatus("エディタで音声認識を開始できませんでした (実機では動きます)");
        }
    }

    void OnKeyword(PhraseRecognizedEventArgs args) => EnqueuePhrase(args.text);

    void StopPlatform()
    {
        if (_keywordRecognizer == null) return;
        _keywordRecognizer.OnPhraseRecognized -= OnKeyword;
        if (_keywordRecognizer.IsRunning) _keywordRecognizer.Stop();
        _keywordRecognizer.Dispose();
        _keywordRecognizer = null;
    }

    void UpdatePlatform() { }

#else
    // ---- その他の環境 (Macエディタなど): 未対応 ----

    void StartPlatform()
    {
        IsRunning = false;
        EnqueueStatus("この環境では音声認識は使えません");
    }

    void StopPlatform() { }
    void UpdatePlatform() { }
#endif
}
