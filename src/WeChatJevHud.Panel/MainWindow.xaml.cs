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
            AnalyzeButton.IsEnabled = true;
        }
    }
}
