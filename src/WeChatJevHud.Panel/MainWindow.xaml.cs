using System.Windows;
using System.Windows.Input;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Panel;

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
    private LiveConversationWatcher? _watcher;

    public MainWindow()
    {
        InitializeComponent();

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
        _watcher.Start();

        ConversationBox.IsReadOnly = true;
        AnalyzeButton.IsEnabled = false;
        SampleButton.IsEnabled = false;
    }

    private void OnLiveUnchecked(object sender, RoutedEventArgs e) => _ = StopLiveAsync();

    private async Task StopLiveAsync()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.StatusChanged -= OnWatcherStatus;
            watcher.RemoteMessageArrived -= OnRemoteMessageArrived;
            watcher.RemoteMessageSkipped -= OnRemoteMessageSkipped;
            await watcher.StopAsync();
            watcher.Dispose();
        }

        ConversationBox.IsReadOnly = false;
        AnalyzeButton.IsEnabled = _client is not null;
        SampleButton.IsEnabled = true;
    }

    private void OnWatcherStatus(object? sender, LiveStatusEventArgs e) =>
        Dispatcher.InvokeAsync(() => StatusText.Text = e.Message);

    private void OnRemoteMessageSkipped(object? sender, LiveSkippedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            // Clear the old cards: leaving them up would attach the previous message's
            // judgment to this one.
            _inFlight?.Cancel();
            CardList.ItemsSource = null;
            TargetText.Text = e.Label;
            StatusText.Text = e.Reason;
        });

    private void OnRemoteMessageArrived(object? sender, LiveMessageEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            ConversationBox.Text = e.Transcript;
            ConversationBox.ScrollToEnd();
            _ = AnalyzeAsync(e.SkippedUntrusted);
        });

    private async Task AnalyzeAsync(int skippedUntrusted = 0)
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
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = "问 Jev 中...";

        try
        {
            var questions = ConversationQuestionSet.Default;
            var result = await _client.AskAsync(input.State, questions, cancellation.Token);

            var cards = new List<JudgmentCard>();
            var missing = new List<string>();
            foreach (var question in questions)
            {
                if (result.Answers.TryGetValue(question.Key, out var answer))
                {
                    cards.Add(JudgmentCard.FromAnswer(question, answer));
                }
                else
                {
                    missing.Add(question.Key);
                }
            }

            CardList.ItemsSource = cards;

            var status =
                $"{result.Model} · {result.Latency.TotalMilliseconds:N0} ms · " +
                $"{result.Usage.InputTokens} in / {result.Usage.OutputTokens} out · {cards.Count} 项判定";
            if (missing.Count > 0)
            {
                // A question that came back unanswered is reported, not quietly dropped.
                status += $" · 未作答: {string.Join(", ", missing)}";
            }

            if (skippedUntrusted > 0)
            {
                // The judgment ran on less context than the screen shows.
                status += $" · {skippedUntrusted} 条因 OCR 不可信未进上下文";
            }

            StatusText.Text = status;
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

    protected override void OnClosed(EventArgs e)
    {
        _inFlight?.Cancel();
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
