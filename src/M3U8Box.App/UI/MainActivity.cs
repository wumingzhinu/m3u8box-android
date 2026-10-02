using System.Text;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using M3U8Box.App.Core;

namespace M3U8Box.App.UI;

/// <summary>
/// The single screen: enter a URL, pick a quality, download.
/// </summary>
/// <remarks>
/// <para>
/// All engine work runs on a background thread and results come back through
/// <see cref="RunOnUiThread"/>. The engine is not documented as thread-safe
/// beyond that, and the UI layer must never block on a network fetch.
/// </para>
/// <para>
/// Output goes to the app's external files dir, so no storage permission is
/// needed and the files are removed with the app. A real release would offer
/// MediaStore export so results land in the shared Movies folder.
/// </para>
/// </remarks>
[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    private EditText _urlInput = null!;
    private EditText _headersInput = null!;
    private EditText _nameInput = null!;
    private Button _inspectButton = null!;
    private Button _downloadButton = null!;
    private ProgressBar _progress = null!;
    private TextView _trackListLabel = null!;
    private RadioGroup _trackGroup = null!;
    private TextView _status = null!;
    private TextView _log = null!;

    private DownloadEngine _engine = null!;
    private InspectResult? _selection;
    private readonly StringBuilder _logLines = new();

    /// <summary>True while a long operation is in flight, to gate the buttons.</summary>
    private bool _busy;

    /// <summary>
    /// Show a crash on screen rather than letting Android kill the process.
    /// </summary>
    /// <remarks>
    /// Without this, a failure in the engine's code path terminates the app and
    /// the user sees the launcher again, which is indistinguishable from the app
    /// simply not having started.
    /// </remarks>
    private void InstallCrashHandler()
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            try
            {
                AndroidLog.UserError("未捕获异常", e.Exception);
                RunOnUiThread(() =>
                {
                    _log?.AppendLine($"未捕获异常: {e.Exception}");
                    _status?.Text = $"错误: {e.Exception.GetType().Name}: {e.Exception.Message}";
                });
            }
            catch
            {
                // Nothing left to do; let the runtime handle it.
            }
            finally
            {
                // Keep the process alive so the message is actually readable.
                e.Handled = true;
            }
        };
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        InstallCrashHandler();

        // Set the view first. Logging setup and directory creation are both
        // fallible on Android, and doing them before the content view meant a
        // failure in either left the user with a blank screen and no clue. Now a
        // problem is reported on-screen instead.
        SetContentView(Resource.Layout.activity_main);

        _urlInput = FindViewById<EditText>(Resource.Id.inputUrl)!;
        _headersInput = FindViewById<EditText>(Resource.Id.inputHeaders)!;
        _nameInput = FindViewById<EditText>(Resource.Id.inputName)!;
        _inspectButton = FindViewById<Button>(Resource.Id.btnInspect)!;
        _downloadButton = FindViewById<Button>(Resource.Id.btnDownload)!;
        _progress = FindViewById<ProgressBar>(Resource.Id.progressBar)!;
        _trackListLabel = FindViewById<TextView>(Resource.Id.trackListLabel)!;
        _trackGroup = FindViewById<RadioGroup>(Resource.Id.trackGroup)!;
        _status = FindViewById<TextView>(Resource.Id.textStatus)!;
        _log = FindViewById<TextView>(Resource.Id.textLog)!;

        SetStatus(GetString(Resource.String.status_idle));

        // The engine logs through Spectre.Console, which assumes a terminal;
        // without this bridge the first download would fail at runtime while
        // building perfectly.
        AndroidLog.Initialize();
        if (AndroidLog.InitError is { } initError)
        {
            AppendLog($"警告: 日志桥接失败 ({initError.GetType().Name}: {initError.Message})");
        }

        try
        {
            // External files dir: writable without permission, and avoids
            // Environment.ProcessPath, which is null on Android.
            var workDir = Path.Combine(CacheDir?.AbsolutePath ?? ".", "work");
            var outputDir = Path.Combine(
                GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir?.AbsolutePath ?? ".",
                "downloads");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(outputDir);

            _engine = new DownloadEngine(workDir, outputDir);
        }
        catch (Exception ex)
        {
            _engine = null!;
            AppendLog($"初始化存储失败: {ex.Message}");
            _inspectButton.Enabled = false;
            SetStatus($"初始化失败: {ex.Message}");
            return;
        }

        _inspectButton.Click += async (_, _) => await InspectAsync();
        _downloadButton.Click += async (_, _) => await DownloadAsync();
    }

    private async Task InspectAsync()
    {
        // _engine is null when storage initialisation failed in OnCreate.
        if (_engine is null)
        {
            AppendLog("引擎未初始化，无法解析");
            return;
        }

        var url = _urlInput.Text?.Trim() ?? string.Empty;
        if (url.Length == 0)
        {
            AppendLog("请先输入链接");
            return;
        }

        var headers = ParseHeaders(_headersInput.Text ?? string.Empty);
        SetBusy(true);
        SetStatus(GetString(Resource.String.status_inspecting));
        AppendLog($"解析 {url}");

        try
        {
            var result = await Task.Run(() => _engine.InspectAsync(
                url, headers, msg => RunOnUiThread(() => AppendLog(msg))));

            _selection = result;
            _engine.RememberUrl(url);

            RunOnUiThread(() =>
            {
                _trackGroup.RemoveAllViews();
                _trackGroup.Visibility = ViewStates.Visible;
                _trackListLabel.Visibility = ViewStates.Visible;
                _trackListLabel.Text = GetString(Resource.String.tracks_found) +
                                       $" ({result.Tracks.Count})" +
                                       (result.IsLive ? " · 直播" : string.Empty);

                // Offer video tracks plus the best audio, since picking a
                // separate audio track is rare and would double the list length.
                var offered = result.VideoTracks.ToList();
                foreach (var audio in result.AudioTracks
                             .GroupBy(t => t.Label.Split('|').LastOrDefault()?.Trim() ?? "audio")
                             .Select(g => g.OrderByDescending(t => t.Bandwidth).First()))
                {
                    offered.Add(audio);
                }

                if (offered.Count == 0)
                {
                    // A media playlist with no variant list still yields tracks,
                    // but guard the case where everything was filtered out.
                    offered.AddRange(result.Tracks);
                }

                for (var i = 0; i < offered.Count; i++)
                {
                    var track = offered[i];
                    var button = new RadioButton(this)
                    {
                        Text = track.Label,
                        Tag = track.Index,
                        TextSize = 13f,
                    };

                    // Default to the highest-bitrate video, matching the order
                    // the tracks were sorted in.
                    if (i == 0)
                    {
                        button.Checked = true;
                    }

                    _trackGroup.AddView(button);
                }

                _downloadButton.Enabled = offered.Count > 0;
                SetStatus(GetString(Resource.String.status_select_track));
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                AppendLog($"解析失败: {ex.Message}");
                SetStatus($"解析失败: {ex.Message}");
                _downloadButton.Enabled = false;
            });
        }
        finally
        {
            RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task DownloadAsync()
    {
        if (_engine is null || _selection is null)
        {
            AppendLog("没有可下载的任务");
            return;
        }

        var checkedId = _trackGroup.CheckedRadioButtonId;
        if (checkedId == View.NoId)
        {
            AppendLog("请选择一个轨道");
            return;
        }

        var button = _trackGroup.FindViewById<RadioButton>(checkedId);

        // Tag is Java.Lang.Object, not int: assigning a boxed int and matching it
        // with `is int` does not compile (CS8121). Java.Lang.Integer is the type
        // the binding actually produces, and .IntValue() unwraps it.
        var trackIndex = (button?.Tag as Java.Lang.Integer)?.IntValue() ?? -1;
        var track = _selection.Tracks.FirstOrDefault(t => t.Index == trackIndex);
        if (track is null)
        {
            AppendLog("选中的轨道无效");
            return;
        }

        var name = _nameInput.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            name = _selection.Title;
        }

        SetBusy(true);
        SetStatus(GetString(Resource.String.status_downloading));
        AppendLog($"开始下载: {track.Label}");

        try
        {
            // Audio is only requested when the user picked an audio track
            // explicitly; otherwise the engine pairs the best per language.
            int? audioIndex = track.MediaType == "AUDIO" ? track.Index : null;

            var output = await Task.Run(() => _engine.DownloadAsync(
                _selection!, videoTrackIndex: track.MediaType == "AUDIO" ? -1 : track.Index,
                audioTrackIndex: audioIndex,
                saveName: name,
                headers: ParseHeaders(_headersInput.Text ?? string.Empty),
                onLog: msg => RunOnUiThread(() => AppendLog(msg))));

            RunOnUiThread(() =>
            {
                AppendLog($"完成: {output}");
                SetStatus($"下载完成: {output}");
                Toast.MakeText(this, "下载完成", ToastLength.Long)?.Show();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                AppendLog($"下载失败: {ex.Message}");
                SetStatus($"下载失败: {ex.Message}");
                Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            });
        }
        finally
        {
            RunOnUiThread(() => SetBusy(false));
        }
    }

    /// <summary>
    /// Parse "Name: value" lines into a header dictionary.
    /// </summary>
    /// <remarks>
    /// Blank lines and lines without a colon are skipped rather than rejected:
    /// a pasted cURL command usually carries a shell continuation line that is
    /// not a header, and failing the whole parse over it would be hostile.
    /// </remarks>
    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Length > 0 && value.Length > 0)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        _inspectButton.Enabled = !busy;

        // The download button keeps whatever state the track list gave it; only
        // the inspect button is gated, since that is what produces tracks.
        if (busy)
        {
            _downloadButton.Enabled = false;
        }
        else
        {
            _downloadButton.Enabled = _selection is not null && _trackGroup.ChildCount > 0;
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AppendLog(string line)
    {
        _logLines.AppendLine(line);

        // Keep the tail only. An unbounded TextView in a ScrollView eventually
        // becomes the main-thread cost of every subsequent update.
        const int MaxLines = 200;
        var all = _logLines.ToString().Split('\n');
        if (all.Length > MaxLines)
        {
            _logLines.Clear();
            foreach (var l in all.Skip(all.Length - MaxLines))
            {
                _logLines.AppendLine(l);
            }
        }

        _log.Text = _logLines.ToString();
    }
}
