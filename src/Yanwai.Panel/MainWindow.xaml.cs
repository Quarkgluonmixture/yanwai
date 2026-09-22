using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yanwai.Overlay;
using Yanwai.TypeSafe;

namespace Yanwai.Panel;

public partial class MainWindow : Window
{
    private const string SampleConversation =
        "对方: 你今天是不是又忘了我跟你说过什么？\n" +
        "我: 记得，你先别提示我，让我自己说。\n" +
        "对方: 那你说。\n" +
        "我: 等一下，我想说完整一点。\n" +
        "对方: 你最好是。";

    private readonly IJevClient? _client;
    private readonly string? _startupError;
    private CancellationTokenSource? _inFlight;
    private OverlayDemo? _demo;

    // Live mode. Everything below is touched on the UI thread only.
    private LiveConversationWatcher? _watcher;
    private OverlayBoard? _board;
    private CancellationTokenSource? _liveCancellation;
    private readonly LinkedList<LiveMessageEventArgs> _queue = new();
    private readonly Dictionary<string, JudgedMessage> _judged = new(StringComparer.Ordinal);
    private readonly ObservableCollection<JudgedMessage> _judgedList = [];
    private bool _pumping;

    public MainWindow()
    {
        InitializeComponent();
        JudgedList.ItemsSource = _judgedList;

        try
        {
            _client = SystemOneClient.FromEnvironment();
        }
        catch (JevException ex)
        {
            _startupError = ex.Message;
            AnalyzeButton.IsEnabled = false;
        }

        StatusText.Text = _startupError ?? "就绪。";
        ConversationBox.Focus();
        PreviewKeyDown += OnPreviewKeyDown;

        if (Environment.GetCommandLineArgs().Contains("--overlay-demo", StringComparer.Ordinal))
        {
            _demo = new OverlayDemo();
            StatusText.Text = _demo.Start();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = AnalyzeAsync();
        }
    }

    private void OnSampleClick(object sender, RoutedEventArgs e)
    {
        ConversationBox.Text = SampleConversation;
        ConversationBox.CaretIndex = ConversationBox.Text.Length;
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

    private void OnLiveChecked(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            LiveToggle.IsChecked = false;
            return;
        }

        var tessdata = LiveConversationWatcher.FindTessdata();
        if (tessdata is null)
        {
            StatusText.Text = "找不到 .ocr-cache\\tessdata。先跑 scripts\\install-ocr-models.ps1。";
            LiveToggle.IsChecked = false;
            return;
        }

        try
        {
            _watcher = new LiveConversationWatcher(tessdata);
        }
        catch (Exception ex)
        {
            // Surfacing the reason matters: a failed OCR engine here would otherwise
            // look identical to "no messages have arrived yet".
            StatusText.Text = $"实时模式启动失败：{ex.Message}";
            LiveToggle.IsChecked = false;
            return;
        }

        _watcher.StatusChanged += OnWatcherStatus;
        _watcher.RemoteMessageArrived += OnRemoteMessageArrived;
        _watcher.RemoteMessageSkipped += OnRemoteMessageSkipped;
        _watcher.WindowObserved += OnWindowObserved;
        _watcher.AnchorsObserved += OnAnchorsObserved;
        _watcher.ConversationReset += OnConversationReset;
        _board = new OverlayBoard();
        _liveCancellation = new CancellationTokenSource();
        _watcher.Start();

        JudgeScreenButton.IsEnabled = true;
        JudgedPanel.Visibility = Visibility.Visible;
        ConversationBox.IsReadOnly = true;
        AnalyzeButton.IsEnabled = false;
        SampleButton.IsEnabled = false;
    }

    private void OnLiveUnchecked(object sender, RoutedEventArgs e) => _ = StopLiveAsync();

    private async Task StopLiveAsync()
    {
        _liveCancellation?.Cancel();
        _liveCancellation?.Dispose();
        _liveCancellation = null;
        _queue.Clear();

        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.StatusChanged -= OnWatcherStatus;
            watcher.RemoteMessageArrived -= OnRemoteMessageArrived;
            watcher.RemoteMessageSkipped -= OnRemoteMessageSkipped;
            watcher.WindowObserved -= OnWindowObserved;
            watcher.AnchorsObserved -= OnAnchorsObserved;
            watcher.ConversationReset -= OnConversationReset;
            await watcher.StopAsync();
            watcher.Dispose();
        }

        _board?.Dispose();
        _board = null;
        ClearJudged();

        JudgeScreenButton.IsEnabled = false;
        JudgeScreenButton.Content = "判定这一屏";
        JudgedPanel.Visibility = Visibility.Collapsed;
        ConversationBox.IsReadOnly = false;
        AnalyzeButton.IsEnabled = _client is not null;
        SampleButton.IsEnabled = true;
    }

    private void OnJudgeScreenClick(object sender, RoutedEventArgs e)
    {
        _watcher?.EnableJudgeScreen();
        JudgeScreenButton.IsEnabled = false;
        JudgeScreenButton.Content = "滚到哪判到哪";
        StatusText.Text = "读取屏幕上对方的消息…之后滚进来的也会自动判定。切回微信后浮层才会显示。";
    }

    private void OnWindowObserved(object? sender, LiveWindowEventArgs e) =>
        Dispatcher.InvokeAsync(() => _board?.Follow(e.Snapshot));

    private void OnAnchorsObserved(object? sender, LiveAnchorsEventArgs e) =>
        Dispatcher.InvokeAsync(() => _board?.UpdateAnchors(e.VisibleBubbles, e.ChatRegion, e.CaptureIsClean));

    private void OnConversationReset(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            // Judgments belong to message ids of the previous conversation; none of them
            // can come back on screen, and listing them would mix two chats.
            _queue.Clear();
            ClearJudged();
        });

    private void OnWatcherStatus(object? sender, LiveStatusEventArgs e) =>
        Dispatcher.InvokeAsync(() => StatusText.Text = e.Message);

    private void OnRemoteMessageSkipped(object? sender, LiveSkippedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            // The earlier judgments stay beside their own bubbles; only say why this
            // one has none.
            StatusText.Text = $"新消息 {e.Label}：{e.Reason}";
        });

    private void OnRemoteMessageArrived(object? sender, LiveMessageEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_judged.ContainsKey(e.MessageId) || _queue.Any(q => q.MessageId == e.MessageId))
            {
                return;
            }

            // A message that just arrived is the one being answered; it jumps the
            // on-screen batch.
            if (e.IsLive)
            {
                _queue.AddFirst(e);
            }
            else
            {
                _queue.AddLast(e);
            }

            _ = PumpAsync();
        });

    /// <summary>
    /// Judges queued messages one at a time. Sequential on purpose: a screenful is a
    /// handful of sub-second calls, and parallel calls would only make a rate limit or
    /// an outage fail several times at once.
    /// </summary>
    private async Task PumpAsync()
    {
        if (_pumping || _client is null)
        {
            return;
        }

        _pumping = true;
        try
        {
            while (_queue.First is { } node && _liveCancellation is { } cancellation)
            {
                _queue.RemoveFirst();
                var item = node.Value;
                if (!ConversationInput.TryParse(item.Transcript, out var input, out var error))
                {
                    StatusText.Text = error;
                    continue;
                }

                var questions = ConversationQuestionSet.For(input.TargetMessage);
                StatusText.Text = _queue.Count == 0 ? "问 Jev 中..." : $"问 Jev 中...（还剩 {_queue.Count} 条）";

                JevResult result;
                try
                {
                    result = await _client.AskAsync(input.State, questions, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (JevException ex)
                {
                    // One failed call must not take the rest of the screen with it.
                    StatusText.Text = ex.Message;
                    continue;
                }

                if (_liveCancellation != cancellation || _board is null)
                {
                    return;
                }

                var judged = JudgedMessage.From(
                    item.MessageId, input.TargetMessage, questions, result, item.SkippedUntrusted);
                _judged[judged.Id] = judged;
                _board.Set(judged.Id, judged.OverlayCard(), judged.Chip());

                if (item.IsLive)
                {
                    _judgedList.Insert(0, judged);
                }
                else
                {
                    _judgedList.Add(judged);
                }

                if (item.IsLive || JudgedList.SelectedItem is null)
                {
                    JudgedList.SelectedItem = judged;
                }

                if (_queue.Count == 0)
                {
                    StatusText.Text = StatusFor(judged);
                }
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void OnJudgedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JudgedList.SelectedItem is not JudgedMessage judged)
        {
            return;
        }

        ShowDetail(judged);
        StatusText.Text = StatusFor(judged);
        _board?.Select(judged.Id);
    }

    private void ClearJudged()
    {
        _judged.Clear();
        _judgedList.Clear();
        _board?.Clear();
        CardList.ItemsSource = null;
        HeadlineRow.Visibility = Visibility.Collapsed;
        TargetText.Text = "—";
    }

    private async Task AnalyzeAsync()
    {
        if (_client is null)
        {
            return;
        }

        if (!ConversationInput.TryParse(ConversationBox.Text, out var input, out var error))
        {
            StatusText.Text = error;
            return;
        }

        _inFlight?.Cancel();
        _inFlight?.Dispose();
        var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        TargetText.Text = input.TargetMessage;
        CardList.ItemsSource = null;
        HeadlineRow.Visibility = Visibility.Collapsed;
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = "问 Jev 中...";

        try
        {
            var questions = ConversationQuestionSet.For(input.TargetMessage);
            var result = await _client.AskAsync(input.State, questions, cancellation.Token);
            var judged = JudgedMessage.From("pasted", input.TargetMessage, questions, result, skippedUntrusted: 0);
            ShowDetail(judged);
            StatusText.Text = StatusFor(judged);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (JevException ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cancellation))
            {
                _inFlight = null;
            }

            cancellation.Dispose();

            // In live mode the button stays disabled; re-enabling it here would hand
            // back a control that edits a box the watcher owns.
            AnalyzeButton.IsEnabled = _watcher is null && _client is not null;
        }
    }

    /// <summary>
    /// Fills the right-hand column. The headline shows nothing at all if either of its
    /// two answers is missing, rather than a half-filled headline.
    /// </summary>
    private void ShowDetail(JudgedMessage judged)
    {
        TargetText.Text = judged.Text;
        CardList.ItemsSource = judged.Cards();

        if (judged.HasHeadline)
        {
            DangerText.Text = judged.Danger;
            AdviceText.Text = judged.AdviceText;
            HeadlineRow.Visibility = Visibility.Visible;
        }
        else
        {
            HeadlineRow.Visibility = Visibility.Collapsed;
        }
    }

    private static string StatusFor(JudgedMessage judged)
    {
        var result = judged.Result;
        var status =
            $"{result.Model} · {result.Latency.TotalMilliseconds:N0} ms · " +
            $"{result.Usage.InputTokens} in / {result.Usage.OutputTokens} out · {result.Answers.Count} 项判定";

        var missing = judged.MissingKeys();
        if (missing.Count > 0)
        {
            // A question that came back unanswered is reported, not quietly dropped.
            status += $" · 未作答: {string.Join(", ", missing)}";
        }

        if (judged.SkippedUntrusted > 0)
        {
            // The judgment ran on less context than the screen shows.
            status += $" · {judged.SkippedUntrusted} 条因 OCR 不可信未进上下文";
        }

        return status;
    }

    protected override void OnClosed(EventArgs e)
    {
        _inFlight?.Cancel();
        _liveCancellation?.Cancel();
        _watcher?.Dispose();
        _board?.Dispose();
        _demo?.Dispose();
        base.OnClosed(e);
    }
}
