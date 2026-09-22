using System.Linq;
using System.Windows;
using System.Windows.Input;
using Yanwai.Core.Geometry;
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
    private LiveConversationWatcher? _watcher;
    private WpfOverlayPresenter? _overlay;
    private CapturePixelRect _pendingAnchor;
    private CapturePixelRect _pendingChatRegion;
    private bool _hasPendingAnchor;
    private OverlayDemo? _demo;

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
        _overlay = new WpfOverlayPresenter();
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
            watcher.WindowObserved -= OnWindowObserved;
            await watcher.StopAsync();
            watcher.Dispose();
        }

        _overlay?.Dispose();
        _overlay = null;
        _hasPendingAnchor = false;

        ConversationBox.IsReadOnly = false;
        AnalyzeButton.IsEnabled = _client is not null;
        SampleButton.IsEnabled = true;
    }

    private void OnWindowObserved(object? sender, LiveWindowEventArgs e) =>
        Dispatcher.InvokeAsync(() => _overlay?.Follow(e.Snapshot));

    private void OnWatcherStatus(object? sender, LiveStatusEventArgs e) =>
        Dispatcher.InvokeAsync(() => StatusText.Text = e.Message);

    private void OnRemoteMessageSkipped(object? sender, LiveSkippedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            // Clear the old cards: leaving them up would attach the previous message's
            // judgment to this one.
            _inFlight?.Cancel();
            CardList.ItemsSource = null;
            HeadlineRow.Visibility = Visibility.Collapsed;
            TargetText.Text = e.Label;
            StatusText.Text = e.Reason;

            // The HUD belonged to the previous message; this one is not it.
            _hasPendingAnchor = false;
            _overlay?.Hide();
        });

    private void OnRemoteMessageArrived(object? sender, LiveMessageEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            ConversationBox.Text = e.Transcript;
            ConversationBox.ScrollToEnd();
            _pendingAnchor = e.Anchor;
            _pendingChatRegion = e.ChatRegion;
            _hasPendingAnchor = true;
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
        HeadlineRow.Visibility = Visibility.Collapsed;
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = "问 Jev 中...";

        try
        {
            var questions = ConversationQuestionSet.For(input.TargetMessage);
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
            ShowHeadline(questions, result);

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

    /// <summary>
    /// Lifts the two answers worth reading first out of the card list. Shows nothing at
    /// all if either is missing, rather than a half-filled headline.
    /// </summary>
    private void ShowHeadline(IReadOnlyList<JevQuestion> questions, JevResult result)
    {
        if (!result.Answers.TryGetValue(ConversationQuestionSet.DangerKey, out var dangerAnswer)
            || dangerAnswer is not ScoreAnswer danger
            || danger.NearestLevel is not { } level)
        {
            return;
        }

        if (!result.Answers.TryGetValue(ConversationQuestionSet.ReplyKey, out var replyAnswer)
            || replyAnswer is not ChoiceAnswer reply
            || reply.Probabilities.Count == 0)
        {
            return;
        }

        var replyQuestion = questions.FirstOrDefault(q => q.Key == ConversationQuestionSet.ReplyKey)
            as ChoiceQuestion;
        var top = reply.Probabilities[0];

        var adviceLabel = replyQuestion?.LabelFor(top.Key) ?? top.Key;

        DangerText.Text = level;
        AdviceText.Text = $"{adviceLabel}  {top.Probability * 100:N0}%";
        HeadlineRow.Visibility = Visibility.Visible;

        ShowOverlay(questions, result, level, adviceLabel, top.Probability);
    }

    /// <summary>
    /// Pushes the same verdict to the HUD beside the bubble. Only in live mode: a pasted
    /// transcript has no bubble on screen to anchor to.
    /// </summary>
    private void ShowOverlay(
        IReadOnlyList<JevQuestion> questions,
        JevResult result,
        string danger,
        string advice,
        double adviceProbability)
    {
        if (_overlay is null || !_hasPendingAnchor)
        {
            return;
        }

        var rows = new List<OverlayRow>(2);
        foreach (var key in new[] { "subtext", "wants" })
        {
            if (!result.Answers.TryGetValue(key, out var answer))
            {
                continue;
            }

            switch (answer)
            {
                case NoulAnswer noul:
                    var question = questions.FirstOrDefault(q => q.Key == key);
                    rows.Add(new OverlayRow(question?.Label ?? key, noul.Probability));
                    break;

                case ChoiceAnswer { Probabilities.Count: > 0 } choice:
                    var choiceQuestion = questions.FirstOrDefault(q => q.Key == key) as ChoiceQuestion;
                    var best = choice.Probabilities[0];
                    rows.Add(new OverlayRow(
                        choiceQuestion?.LabelFor(best.Key) ?? best.Key,
                        best.Probability));
                    break;
            }
        }

        _overlay.Show(
            new OverlayContent(
                TargetText.Text,
                danger,
                $"{advice}  {adviceProbability * 100:N0}%",
                rows),
            _pendingAnchor,
            _pendingChatRegion);
    }

    protected override void OnClosed(EventArgs e)
    {
        _inFlight?.Cancel();
        _watcher?.Dispose();
        _overlay?.Dispose();
        _demo?.Dispose();
        base.OnClosed(e);
    }
}
