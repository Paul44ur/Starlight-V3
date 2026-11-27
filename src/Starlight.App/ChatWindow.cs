using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.AI;
using Microsoft.Win32;
using Starlight.Agent;
using Starlight.Events;
using Starlight.Memory;
using Starlight.Skills;
using Starlight.Storage;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Starlight.App;

/// <summary>
/// The chat window (plan 5.6): conversations on the left, the conversation on the right with native Markdown
/// and tool steps, a message box with attachments, and the Activity and Statistics views.
/// </summary>
internal sealed class ChatWindow : FluentWindow
{
    private const int MaxTextAttachment = 100_000;
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".json", ".log", ".xml", ".yaml", ".yml", ".ini", ".cs", ".py", ".js", ".ts", ".html", ".css", ".ps1", ".bat", ".sql"];

    private readonly AssistantCore _core;
    private readonly MemoryStore _memory;
    private readonly SkillStore _skills;
    private readonly Action _openSettings;
    private readonly System.Windows.Controls.ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _search = new() { PlaceholderText = "Search conversations", Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _messages = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, PlaceholderText = "Ask Starlight…  (Enter sends, Shift+Enter for a new line)", VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = "Send", Appearance = ControlAppearance.Primary, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
    private readonly WrapPanel _attachmentChips = new();
    private readonly List<(string Name, AIContent Content)> _attachments = [];
    private readonly ContentControl _view = new();
    private readonly Grid _chatView;
    private readonly List<IDisposable> _subs = [];
    private Conversation? _conversation;
    private string? _liveTurn;
    private TextBlock? _liveAnswer;

    public ChatWindow(AssistantCore core, MemoryStore memory, SkillStore skills, Action openSettings)
    {
        _core = core;
        _memory = memory;
        _skills = skills;
        _openSettings = openSettings;
        Title = "Starlight";
        Width = 1040;
        Height = 720;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        _chatView = BuildChatView();

        var left = new DockPanel { Margin = new Thickness(12, 0, 8, 12) };
        var newChat = new Button { Content = "New conversation", HorizontalAlignment = HorizontalAlignment.Stretch, Icon = new SymbolIcon(SymbolRegular.Add24) };
        newChat.Click += (_, _) => Show(_core.Conversations.New());
        DockPanel.SetDock(newChat, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(newChat);
        left.Children.Add(_search);
        left.Children.Add(_list);
        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: ConversationInfo info } && info.Id != _conversation?.Id)
            {
                Show(_core.Conversations.Get(info.Id));
            }
        };

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        nav.Children.Add(NavButton("Chat", () => _view.Content = _chatView));
        nav.Children.Add(NavButton("Memory", () => _view.Content = MemoryView()));
        nav.Children.Add(NavButton("Activity", () => _view.Content = ActivityView()));
        nav.Children.Add(NavButton("Statistics", () => _view.Content = StatisticsView()));
        nav.Children.Add(NavButton("Settings", _openSettings));
        _view.Content = _chatView;
        var right = new DockPanel { Margin = new Thickness(8, 0, 16, 12) };
        DockPanel.SetDock(nav, Dock.Top);
        right.Children.Add(nav);
        right.Children.Add(_view);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        var root = new DockPanel();
        var titleBar = new TitleBar { Title = "Starlight" };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        Content = root;

        _subs.Add(core.Events.Subscribe<TurnStarted>(e => Dispatcher.BeginInvoke(() => OnTurnStarted(e))));
        _subs.Add(core.Events.Subscribe<AnswerDelta>(e => Dispatcher.BeginInvoke(() => OnDelta(e))));
        _subs.Add(core.Events.Subscribe<ToolStarted>(e => Dispatcher.BeginInvoke(() => OnTool(e))));
        _subs.Add(core.Events.Subscribe<AnswerCompleted>(e => Dispatcher.BeginInvoke(() => OnCompleted(e))));
        _subs.Add(core.Events.Subscribe<SkillOffered>(e => Dispatcher.BeginInvoke(() =>
        {
            if (_conversation?.Id == e.ConversationId && _liveTurn is null && _view.Content == _chatView)
            {
                Render();
            }
        })));
        Closed += (_, _) => _subs.ForEach(s => s.Dispose());

        RefreshList();
        Show(core.Conversations.Current ?? FirstOrNew());
    }

    private Conversation FirstOrNew()
    {
        var latest = _core.Db.ListConversations(limit: 1).FirstOrDefault();
        return latest is null ? _core.Conversations.New() : _core.Conversations.Get(latest.Id);
    }

    private static Button NavButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Grid BuildChatView()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var attach = new Button { Icon = new SymbolIcon(SymbolRegular.Attach24), ToolTip = "Attach an image or a text file", Margin = new Thickness(0, 0, 8, 0) };
        attach.Click += (_, _) => Attach();
        _send.Click += async (_, _) => await SendOrStopAsync();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                await SendOrStopAsync();
            }
        };
        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(attach, Dock.Left);
        DockPanel.SetDock(_send, Dock.Right);
        bar.Children.Add(attach);
        bar.Children.Add(_send);
        bar.Children.Add(_input);
        var bottom = new StackPanel();
        bottom.Children.Add(_attachmentChips);
        bottom.Children.Add(bar);
        Grid.SetRow(bottom, 1);
        grid.Children.Add(_scroll);
        grid.Children.Add(bottom);
        return grid;
    }

    public void Show(Conversation c)
    {
        _conversation = c;
        _view.Content = _chatView;
        Render();
        // Cleared first: after "New conversation" the old entry must be clickable again.
        _list.SelectedItem = null;
        foreach (ListBoxItem item in _list.Items)
        {
            if (item.Tag is ConversationInfo info && info.Id == c.Id)
            {
                _list.SelectedItem = item;
            }
        }
    }

    private void RefreshList()
    {
        var q = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim();
        _list.Items.Clear();
        foreach (var info in _core.Db.ListConversations(q))
        {
            var item = new ListBoxItem
            {
                Tag = info,
                Content = new TextBlock { Text = (info.Pinned ? "📌 " : "") + info.Title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{info.Title}\n{info.Updated:g}" },
                Padding = new Thickness(8, 6, 8, 6),
            };
            var menu = new ContextMenu();
            var pin = new System.Windows.Controls.MenuItem { Header = info.Pinned ? "Unpin" : "Pin" };
            pin.Click += (_, _) => { _core.Db.PinConversation(info.Id, !info.Pinned); RefreshList(); };
            var rename = new System.Windows.Controls.MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => await RenameAsync(info);
            var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
            delete.Click += async (_, _) => await DeleteAsync(info);
            var forget = new System.Windows.Controls.MenuItem { Header = "Forget what was learned here" };
            forget.Click += async (_, _) => await ForgetLearnedAsync(info);
            menu.Items.Add(pin);
            menu.Items.Add(rename);
            menu.Items.Add(forget);
            menu.Items.Add(delete);
            item.ContextMenu = menu;
            _list.Items.Add(item);
        }
    }

    private async Task RenameAsync(ConversationInfo info)
    {
        var box = new TextBox { Text = info.Title, MinWidth = 320 };
        var dialog = new Wpf.Ui.Controls.MessageBox { Title = "Rename conversation", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel" };
        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary && box.Text.Trim().Length > 0)
        {
            _core.Db.RenameConversation(info.Id, box.Text.Trim());
            _core.Conversations.Get(info.Id).Title = box.Text.Trim();
            RefreshList();
        }
    }

    private async Task DeleteAsync(ConversationInfo info)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox { Title = "Delete conversation", Content = $"Delete \"{info.Title}\"? This can't be undone.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel" };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }
        _core.Db.DeleteConversation(info.Id);
        _core.Conversations.Forget(info.Id);
        RefreshList();
        if (_conversation?.Id == info.Id)
        {
            Show(FirstOrNew());
        }
    }

    private async Task ForgetLearnedAsync(ConversationInfo info)
    {
        var count = _memory.All().Count(f => f.Conversation == info.Id);
        if (count == 0)
        {
            await new Wpf.Ui.Controls.MessageBox { Title = "Forget this conversation", Content = $"Nothing was remembered from \"{info.Title}\".", CloseButtonText = "OK" }.ShowDialogAsync();
            return;
        }
        var dialog = new Wpf.Ui.Controls.MessageBox { Title = "Forget this conversation", Content = $"Forget the {count} fact(s) Starlight learned in \"{info.Title}\"?", PrimaryButtonText = "Forget", CloseButtonText = "Cancel" };
        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            _memory.ForgetConversation(info.Id);
        }
    }

    // ---- rendering ----

    private Brush Fg => (Brush)(TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.White);
    private Brush Subtle => (Brush)(TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray);
    private Brush CardBg => (Brush)(TryFindResource("ControlFillColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)));
    private Brush AccentBg => (Brush)(TryFindResource("AccentFillColorDefaultBrush") ?? Brushes.SteelBlue);

    private void Render()
    {
        _messages.Children.Clear();
        _liveAnswer = null;
        if (_conversation is null)
        {
            return;
        }
        var results = _conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .GroupBy(r => r.CallId ?? "").ToDictionary(g => g.Key, g => g.Last().Result?.ToString() ?? "");
        foreach (var m in _conversation.Messages)
        {
            if (m.Role == ChatRole.User)
            {
                if (m.Text.StartsWith("(Picture from", StringComparison.Ordinal) || m.Text.StartsWith("Summary of our earlier conversation", StringComparison.Ordinal))
                {
                    continue;
                }
                var images = m.Contents.OfType<DataContent>().Count();
                AddUser(m.Text + (images > 0 ? $"\n[{images} picture{(images > 1 ? "s" : "")}]" : ""));
            }
            else if (m.Role == ChatRole.Assistant)
            {
                foreach (var call in m.Contents.OfType<FunctionCallContent>())
                {
                    AddToolStep(call.Name, JsonSerializer.Serialize(call.Arguments), results.GetValueOrDefault(call.CallId ?? ""));
                }
                if (!string.IsNullOrWhiteSpace(m.Text) && m.Text != "Understood.")
                {
                    AddAssistant(m.Text);
                }
            }
        }
        foreach (var draft in _skills.Drafts(_conversation.Id))
        {
            AddSkillOffer(draft);
        }
        if (_messages.Children.Count == 0)
        {
            _messages.Children.Add(new TextBlock { Text = "Ask anything, or tell Starlight what to do on this PC.", Foreground = Subtle, Margin = new Thickness(4, 24, 0, 0), FontSize = 15 });
        }
        ScrollToEnd();
    }

    /// <summary>The offer to keep a turn's steps as a skill. Saved only when the user presses Save (plan 5.10).</summary>
    private void AddSkillOffer(SkillDraft draft)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Save these steps as a skill?", Foreground = Fg, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Then Starlight can do this again by name.", Foreground = Subtle, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (step, i) in draft.Steps.Select((st, i) => (st, i)))
        {
            panel.Children.Add(new TextBlock { Text = $"{i + 1}. {step.Tool}  {Short(step.ArgumentsJson, 120)}", Foreground = Fg, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        if (draft.Parameters.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Asks each time for: " + string.Join(", ", draft.Parameters), Foreground = Subtle, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        }
        var name = new TextBox { Text = Starlight.Skills.SkillRecorder.SuggestNameFor(draft.Request), Width = 280, Margin = new Thickness(0, 8, 8, 0) };
        var save = new Button { Content = "Save skill", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 8, 8, 0) };
        var dismiss = new Button { Content = "Not now", Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) =>
        {
            if (SkillStore.SkillName(name.Text).Length == 0)
            {
                return;
            }
            try
            {
                _skills.Approve(draft.Id, name.Text);
            }
            catch (InvalidOperationException ex)
            {
                name.ToolTip = ex.Message;
                name.BorderBrush = Brushes.IndianRed;
                return;
            }
            Render();
        };
        dismiss.Click += (_, _) => { _skills.DropDraft(draft.Id); Render(); };
        var row = new WrapPanel();
        row.Children.Add(name);
        row.Children.Add(save);
        row.Children.Add(dismiss);
        panel.Children.Add(row);
        _messages.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 6, 80, 6), Child = panel });
    }

    private void AddUser(string text) => _messages.Children.Add(new Border
    {
        Background = AccentBg,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 8, 12, 8),
        Margin = new Thickness(80, 6, 0, 6),
        HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14 },
    });

    private void AddAssistant(string markdown)
    {
        var box = new System.Windows.Controls.RichTextBox
        {
            Document = MarkdownView.Render(markdown, Fg, CardBg),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsDocumentEnabled = true,
            Margin = new Thickness(0, 6, 80, 6),
            Padding = new Thickness(0),
        };
        _messages.Children.Add(box);
    }

    private void AddToolStep(string tool, string args, string? result)
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = $"⚙ {tool}  {Short(args, 90)}", Foreground = Subtle, FontSize = 12.5 },
            Content = new TextBlock { Text = $"Arguments: {args}\n\nResult: {result ?? "(running)"}", TextWrapping = TextWrapping.Wrap, Foreground = Subtle, FontSize = 12.5, Margin = new Thickness(20, 4, 0, 4) },
            Margin = new Thickness(0, 2, 80, 2),
        };
        _messages.Children.Add(expander);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void ScrollToEnd() => Dispatcher.BeginInvoke(_scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);

    // ---- live turn ----

    private void OnTurnStarted(TurnStarted e)
    {
        if (e.ConversationId != _conversation?.Id)
        {
            return;
        }
        _liveTurn = e.TurnId;
        if (e.Source == "voice")
        {
            AddUser(e.Text);
        }
        _liveAnswer = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Fg, FontSize = 14, Margin = new Thickness(0, 6, 80, 6), Text = "…" };
        _messages.Children.Add(_liveAnswer);
        _send.Content = "Stop";
        ScrollToEnd();
    }

    private void OnDelta(AnswerDelta e)
    {
        if (e.TurnId != _liveTurn || _liveAnswer is null)
        {
            return;
        }
        _liveAnswer.Text = _liveAnswer.Text == "…" ? e.Delta : _liveAnswer.Text + e.Delta;
        ScrollToEnd();
    }

    private void OnTool(ToolStarted e)
    {
        if (e.TurnId != _liveTurn)
        {
            return;
        }
        var index = _liveAnswer is null ? _messages.Children.Count : _messages.Children.IndexOf(_liveAnswer);
        _messages.Children.Insert(index, new TextBlock { Text = $"⚙ {e.Tool}  {Short(e.ArgumentsJson, 90)}", Foreground = Subtle, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) });
    }

    private void OnCompleted(AnswerCompleted e)
    {
        if (e.TurnId != _liveTurn)
        {
            RefreshList();
            return;
        }
        _liveTurn = null;
        _send.Content = "Send";
        Render();
        if (e.Failed)
        {
            _messages.Children.Add(new TextBlock { Text = e.Text, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 80, 6) });
            ScrollToEnd();
        }
        RefreshList();
    }

    private async Task SendOrStopAsync()
    {
        if (_liveTurn is not null)
        {
            _core.Agent?.CancelCurrent();
            return;
        }
        var text = _input.Text.Trim();
        if ((text.Length == 0 && _attachments.Count == 0) || _core.Agent is null)
        {
            return;
        }
        var conversation = _conversation ??= _core.Conversations.New();
        _core.Conversations.Use(conversation);
        var extra = new List<AIContent>();
        var full = new StringBuilder(text);
        foreach (var (name, content) in _attachments)
        {
            if (content is TextContent t)
            {
                full.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n\nAttached file {name}:\n```\n{t.Text}\n```");
            }
            else
            {
                extra.Add(content);
            }
        }
        _input.Text = "";
        _attachments.Clear();
        _attachmentChips.Children.Clear();
        if (_messages.Children.Count == 1 && _messages.Children[0] is TextBlock)
        {
            _messages.Children.Clear();
        }
        AddUser(text + (extra.Count > 0 ? $"\n[{extra.Count} picture{(extra.Count > 1 ? "s" : "")}]" : ""));
        await _core.Agent.RunAsync(conversation, full.ToString(), "chat", attachments: extra.Count > 0 ? extra : null);
    }

    private void Attach()
    {
        var dialog = new OpenFileDialog { Title = "Attach files", Multiselect = true, Filter = "Images and text|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;" + string.Join(';', TextExtensions.Select(e => "*" + e)) + "|All files|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        foreach (var file in dialog.FileNames)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var name = Path.GetFileName(file);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            {
                if (string.IsNullOrWhiteSpace(_core.Settings.Current.Model.VisionProjectorFile))
                {
                    Chip($"{name}: the model can't see pictures (no vision projector in Settings)", null);
                    continue;
                }
                var mime = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".bmp" => "image/bmp", ".gif" => "image/gif", _ => "image/png" };
                try
                {
                    if (new FileInfo(file).Length > MaxPictureAttachment)
                    {
                        Chip($"{name}: pictures can be up to 10 MB", null);
                        continue;
                    }
                    Chip(name, new DataContent(File.ReadAllBytes(file), mime));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Chip($"{name}: could not be read ({ex.Message})", null);
                }
            }
            else if (TextExtensions.Contains(ext))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxTextAttachment)
                    {
                        Chip($"{name}: text files can be up to 100 KB", null);
                        continue;
                    }
                    Chip(name, new TextContent(File.ReadAllText(file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Chip($"{name}: could not be read ({ex.Message})", null);
                }
            }
            else
            {
                Chip($"{name}: only pictures and text files up to 100 KB can be attached", null);
            }
        }
    }

    private const long MaxPictureAttachment = 10 * 1024 * 1024;

    private void Chip(string label, AIContent? content)
    {
        if (content is not null)
        {
            _attachments.Add((label, content));
        }
        var chip = new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock { Text = (content is null ? "⚠ " : "📎 ") + label, Foreground = content is null ? Brushes.IndianRed : Fg, FontSize = 12.5 },
        };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            _attachmentChips.Children.Remove(chip);
            _attachments.RemoveAll(a => a.Name == label);
        };
        chip.ToolTip = "Click to remove";
        _attachmentChips.Children.Add(chip);
    }

    // ---- Activity and Statistics ----

    private DockPanel MemoryView()
    {
        var search = new TextBox { PlaceholderText = "Search memory", Margin = new Thickness(0, 0, 0, 8) };
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var facts = _memory.All(string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim());
            if (facts.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(search.Text) ? "Nothing remembered yet." : "No fact matches.", Foreground = Subtle });
            }
            foreach (var f in facts)
            {
                var row = new DockPanel();
                var delete = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), ToolTip = "Forget this", Appearance = ControlAppearance.Transparent, VerticalAlignment = VerticalAlignment.Center };
                delete.Click += (_, _) => { _memory.Forget(f.Id); Fill(); };
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = f.Text, Foreground = Fg, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = $"{f.Topic} · {f.Updated:d MMM yyyy}", Foreground = Subtle, FontSize = 12 });
                row.Children.Add(text);
                list.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6), Child = row });
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        var skillList = new StackPanel();
        void FillSkills()
        {
            skillList.Children.Clear();
            skillList.Children.Add(new TextBlock { Text = "Skills", Foreground = Fg, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            var skills = _skills.All();
            if (skills.Count == 0)
            {
                skillList.Children.Add(new TextBlock { Text = "No skills yet. After a task with several steps, Starlight offers to save it here.", Foreground = Subtle, TextWrapping = TextWrapping.Wrap });
            }
            foreach (var sk in skills)
            {
                var row = new DockPanel();
                var delete = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), ToolTip = "Delete this skill", Appearance = ControlAppearance.Transparent, VerticalAlignment = VerticalAlignment.Center };
                delete.Click += (_, _) => { _skills.Delete(sk.Name); FillSkills(); };
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = $"{sk.Name}({string.Join(", ", sk.Parameters)})", Foreground = Fg });
                text.Children.Add(new TextBlock { Text = $"{sk.Steps.Count} steps: {string.Join(", ", sk.Steps.Select(st => st.Tool))} · first made for \"{Short(sk.Description, 80)}\"", Foreground = Subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(text);
                skillList.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6), Child = row });
            }
        }
        FillSkills();
        var both = new StackPanel();
        both.Children.Add(list);
        both.Children.Add(skillList);
        var panel = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "What Starlight remembers about you, and the skills you saved. Delete anything you don't want kept.", Foreground = Subtle, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(search);
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer { Content = both, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return panel;
    }

    private DockPanel ActivityView()
    {
        var view = new ListView();
        foreach (var e in _core.Db.RecentAudit(300))
        {
            view.Items.Add(new TextBlock
            {
                Text = $"{e.Time:dd MMM HH:mm:ss}  {(e.Ok ? "✓" : "✗")} {e.Tool}  {Short(e.ArgumentsJson, 100)}\n      {e.Source}, {(e.ApprovedBy == "user" ? "approved by you" : e.ApprovedBy == "auto" ? "safe" : e.ApprovedBy)}: {Short(e.Result.Replace('\n', ' '), 160)}",
                Foreground = e.Ok ? Fg : Brushes.IndianRed,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        var panel = new DockPanel();
        var head = new TextBlock { Text = "Every action Starlight took, newest first.", Foreground = Subtle, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(view.Items.Count == 0 ? new TextBlock { Text = "Nothing yet.", Foreground = Subtle } : view);
        return panel;
    }

    private ScrollViewer StatisticsView()
    {
        var since = DateTimeOffset.Now.AddDays(-7);
        var turns = _core.Db.Stats("turn.seconds", since);
        var speed = _core.Db.Stats("model.tokens_per_second", since);
        var first = _core.Db.Stats("model.first_token_seconds", since);
        var audit = _core.Db.RecentAudit(5000).Where(a => a.Time >= since).ToList();
        static string Median(List<StatRow> rows, string unit, string format)
        {
            if (rows.Count == 0)
            {
                return "–";
            }
            var v = rows.Select(r => r.Value).Order().ToList();
            return v[v.Count / 2].ToString(format, System.Globalization.CultureInfo.CurrentCulture) + unit;
        }
        var wrap = new WrapPanel();
        void Card(string title, string value, string? note = null)
        {
            var p = new StackPanel();
            p.Children.Add(new TextBlock { Text = title, Foreground = Subtle, FontSize = 12.5 });
            p.Children.Add(new TextBlock { Text = value, Foreground = Fg, FontSize = 26, FontWeight = FontWeights.SemiBold });
            if (note is not null)
            {
                p.Children.Add(new TextBlock { Text = note, Foreground = Subtle, FontSize = 12 });
            }
            wrap.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 12, 12), MinWidth = 190, Child = p });
        }
        Card("Requests", turns.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), $"{turns.Count(t => t.Detail == "voice")} by voice");
        Card("Time per request", Median(turns, " s", "0.0"), "median");
        Card("Actions", audit.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), $"{audit.Count(a => !a.Ok)} failed or declined");
        Card("Model speed", Median(speed, " tokens/s", "0"), "median");
        Card("First word after", Median(first, " s", "0.00"), "median");
        var top = audit.GroupBy(a => a.Tool).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}: {g.Count()}");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "The last 7 days.", Foreground = Subtle, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(wrap);
        panel.Children.Add(new TextBlock { Text = "Most used tools: " + (audit.Count == 0 ? "none yet" : string.Join(", ", top)), Foreground = Fg, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = panel };
    }
}
