using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using RhiGhAI.Core;
using RhiGhAI.Core.Codex;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Persistence;
using RhiGhAI.Core.Providers;
using RhiGhAI.Rhino.Services;
using Rhino;
using Rhino.DocObjects;

namespace RhiGhAI.Rhino.UI;

[Guid("E7C5B31B-ADCA-4511-A57D-D3BB37CCF725")]
public sealed class RhiGhAIPanel : Panel
{
    // A docked Rhino panel is usually 320–420 px wide; below this the header and footer drop their labels.
    private const int CompactWidth = 400;

    private readonly RhiGhAIService _service;
    private readonly StackLayout _messages = new()
    {
        Spacing = 12,
        Padding = new Padding(14, 14),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    private readonly Scrollable _messageScroll;
    private readonly Label _selection = new();
    private readonly Label _agentState = new();
    private readonly Label _footer = new();
    private readonly Label _brandSubtitle;
    private readonly TextArea _prompt = new();
    private readonly DropDown _source = new();
    private readonly DropDown _target = new();
    private readonly DropDown _model = new();
    private readonly DropDown _effort = new();
    private bool _syncingSource;
    private bool _announcedProviderSetup;
    private readonly RhiGhAIButton _send;
    private readonly RhiGhAIButton _newChat;
    private readonly RhiGhAIButton _settings;
    private IReadOnlyList<ProviderModel> _models = Array.Empty<ProviderModel>();
    private ConnectionSnapshot _connection;
    private bool _busy;
    private bool _compact;
    private RuntimeState? _announcedRuntimeState;
    private bool _announcedLoginRequired;

    public RhiGhAIPanel()
    {
        _service = RhiGhAIPlugIn.Instance?.Service ?? throw new InvalidOperationException("RhiGhAI plugin is not loaded.");
        _connection = _service.Connection;
        BackgroundColor = RhiGhAIStyles.Section;
        _messageScroll = new Scrollable
        {
            Content = _messages,
            Border = BorderType.None,
            BackgroundColor = RhiGhAIStyles.Section,
            ExpandContentWidth = true
        };
        _brandSubtitle = RhiGhAIStyles.MachineLabel($"Rhino × Grasshopper · v{ProductInfo.Version}", RhiGhAIStyles.Lime);
        _send = RhiGhAIStyles.Button("ОТПРАВИТЬ", BrandButtonKind.Pink, 132);
        _newChat = RhiGhAIStyles.Button("＋ ЗАНОВО", BrandButtonKind.Ghost, 104);
        _settings = RhiGhAIStyles.Button("НАСТРОЙКИ", BrandButtonKind.Ghost, 112);
        Content = BuildShell();

        _service.Message += ServiceOnMessage;
        _service.ConnectionChanged += ServiceOnConnectionChanged;
        _service.BusyChanged += ServiceOnBusyChanged;
        RhinoDoc.SelectObjects += SelectionChanged;
        RhinoDoc.DeselectObjects += SelectionChanged;
        RhinoDoc.DeselectAllObjects += SelectionCleared;
        RhinoDoc.ActiveDocumentChanged += ActiveDocumentChanged;

        SetAgentState("готов к задаче", RhiGhAIStyles.Lime);
        UpdateSelection();
        ApplyConnection(_connection);
        _ = RestoreTranscriptSafeAsync(RhinoDoc.ActiveDoc?.Path);
        _ = RefreshConnectionSafeAsync();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width > 0)
        {
            // Without an explicit width the stack measures at its natural size, wrapped labels never
            // wrap and the feed grows a horizontal scrollbar instead of following the panel.
            _messages.Width = Math.Max(160, Width - 20);
        }

        bool compact = Width > 0 && Width < CompactWidth;
        if (compact == _compact)
        {
            return;
        }

        _compact = compact;
        _brandSubtitle.Visible = !compact;
        SetButton(_newChat, compact ? "＋" : "＋ ЗАНОВО", compact ? 38 : 104);
        SetButton(_settings, compact ? "⚙" : "НАСТРОЙКИ", compact ? 38 : 112);
        UpdateFooter();
        UpdateSelection();
    }

    private Control BuildShell()
    {
        _newChat.Click += (_, _) =>
        {
            if (_busy)
            {
                _service.Stop();
                return;
            }

            _messages.Items.Clear();
            _service.StartNewConversation(RhinoDoc.ActiveDoc);
        };
        _settings.Click += (_, _) => ShowSettings();

        StackLayout brandTitle = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "RHIGH", Font = RhiGhAIStyles.Display(15), TextColor = RhiGhAIStyles.White },
                new Label { Text = "AI", Font = RhiGhAIStyles.Display(15), TextColor = RhiGhAIStyles.Orange }
            }
        };
        StackLayout brand = new() { Spacing = 1, Items = { brandTitle, _brandSubtitle } };
        TableLayout header = new()
        {
            BackgroundColor = RhiGhAIStyles.Dark,
            Padding = new Padding(14, 10),
            Spacing = new Size(8, 0),
            Rows =
            {
                new TableRow(
                    new RhiGhAIStairs(RhiGhAIStyles.Orange),
                    brand,
                    new TableCell(null, true),
                    _newChat,
                    _settings)
            }
        };

        _selection.Font = RhiGhAIStyles.Mono(8.5f);
        _selection.TextColor = RhiGhAIStyles.Muted;
        _selection.VerticalAlignment = VerticalAlignment.Center;
        _agentState.Font = RhiGhAIStyles.Mono(8.5f);
        _agentState.TextColor = RhiGhAIStyles.Muted;
        _agentState.VerticalAlignment = VerticalAlignment.Center;
        _agentState.TextAlignment = TextAlignment.Right;
        Panel context = new()
        {
            BackgroundColor = RhiGhAIStyles.Input,
            Padding = new Padding(14, 6),
            Content = new TableLayout
            {
                Spacing = new Size(10, 0),
                Rows = { new TableRow(new TableCell(_selection, true), new TableCell(_agentState)) }
            }
        };

        _footer.BackgroundColor = RhiGhAIStyles.Dark;
        _footer.TextColor = Color.FromArgb(205, 255, 255, 255);
        _footer.Font = RhiGhAIStyles.Mono(8.5f);
        _footer.VerticalAlignment = VerticalAlignment.Center;
        Panel footerPanel = new() { BackgroundColor = RhiGhAIStyles.Dark, Padding = new Padding(14, 6), Content = _footer };

        return new TableLayout
        {
            Spacing = Size.Empty,
            Rows =
            {
                new TableRow(header),
                new TableRow(new TableCell(_messageScroll, true)) { ScaleHeight = true },
                new TableRow(context),
                new TableRow(BuildComposer()),
                new TableRow(footerPanel)
            }
        };
    }

    private Control BuildComposer()
    {
        _prompt.Font = RhiGhAIStyles.Ui(10.5f);
        _prompt.BackgroundColor = RhiGhAIStyles.White;
        _prompt.TextColor = RhiGhAIStyles.Ink;
        _prompt.Height = 74;
        _prompt.Wrap = true;
        _prompt.KeyDown += (_, args) =>
        {
            if (args.Key == Keys.Enter && args.Modifiers.HasFlag(Keys.Control))
            {
                args.Handled = true;
                _ = SendOrStopSafeAsync();
            }
        };

        _target.Items.Add("Rhino");
        _target.Items.Add("Grasshopper");
        _target.SelectedIndex = _service.Settings.DefaultTarget == TargetHost.Rhino ? 0 : 1;
        _target.SelectedIndexChanged += (_, _) =>
        {
            // Nothing used to write DefaultTarget, so the composer always reopened on Rhino.
            TargetHost chosen = _target.SelectedIndex == 1 ? TargetHost.Grasshopper : TargetHost.Rhino;
            if (chosen != _service.Settings.DefaultTarget)
            {
                _ = SaveSettingsSafeAsync(new SettingsSaved(_service.Settings with { DefaultTarget = chosen }, null));
            }
        };
        _source.Items.Add("Codex · ChatGPT");
        _source.Items.Add("API-ключ");
        SyncSource(_service.Settings.Provider);
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingSource)
            {
                _ = SwitchProviderSafeAsync();
            }
        };
        foreach (DropDown dropDown in new[] { _source, _target, _model, _effort })
        {
            dropDown.Font = RhiGhAIStyles.Ui(9.5f);
            dropDown.Height = 28;
        }

        _model.SelectedIndexChanged += (_, _) => UpdateEfforts();
        _send.Click += async (_, _) => await SendOrStopSafeAsync();
        _send.Height = 30;

        // Two fixed rows instead of one wide row: the same grid works at 300 px and at 900 px.
        TableLayout firstRow = new()
        {
            Spacing = new Size(6, 0),
            Rows = { new TableRow(new TableCell(_target, false), new TableCell(_model, true)) }
        };
        TableLayout secondRow = new()
        {
            Spacing = new Size(6, 0),
            Rows = { new TableRow(new TableCell(_effort, false), new TableCell(null, true), new TableCell(_send)) }
        };
        _target.Width = 116;
        _effort.Width = 116;

        // Switching provider is the one setting worth having in reach: subscription limits run out
        // mid-task. Keys, addresses and account actions stay in the settings window.
        TableLayout sourceRow = new()
        {
            Spacing = new Size(8, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(new Label
                    {
                        Text = "ИСТОЧНИК",
                        Font = RhiGhAIStyles.Mono(8.5f),
                        TextColor = RhiGhAIStyles.Muted,
                        VerticalAlignment = VerticalAlignment.Center
                    }),
                    new TableCell(_source, true))
            }
        };

        StackLayout box = new()
        {
            Spacing = 8,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                sourceRow,
                new Panel { BackgroundColor = RhiGhAIStyles.Line, Height = 1 },
                RhiGhAIStyles.Micro("Что сделать в модели", RhiGhAIStyles.Muted),
                _prompt,
                firstRow,
                secondRow
            }
        };

        return new Panel
        {
            BackgroundColor = RhiGhAIStyles.Section,
            Padding = new Padding(12, 10),
            Content = RhiGhAIStyles.Bordered(box, RhiGhAIStyles.Ink, RhiGhAIStyles.White, new Padding(10))
        };
    }

    private void ShowSettings()
    {
        if (_busy)
        {
            AddMessage(new ServiceMessage("system", "Сначала остановите текущую задачу, затем откройте настройки."));
            return;
        }

        RhiGhAISettingsDialog dialog = new(_service, SelectedModelId(), SelectedEffortId());
        dialog.Saved += (_, saved) => _ = SaveSettingsSafeAsync(saved);
        dialog.ShowModal(this);
    }

    private void SyncSource(ProviderKind provider)
    {
        _syncingSource = true;
        _source.SelectedIndex = provider == ProviderKind.Codex ? 0 : 1;
        _syncingSource = false;
    }

    private Task SwitchProviderSafeAsync()
    {
        ProviderKind provider = _source.SelectedIndex == 1 ? ProviderKind.OpenAiCompatible : ProviderKind.Codex;
        if (provider == _service.Settings.Provider)
        {
            return Task.CompletedTask;
        }

        _announcedProviderSetup = false;
        AddMessage(new ServiceMessage("progress", provider == ProviderKind.Codex ? "Переключаюсь на Codex…" : "Переключаюсь на API-провайдера…"));
        return SaveSettingsSafeAsync(new SettingsSaved(_service.Settings with { Provider = provider }, null));
    }

    private async Task SaveSettingsSafeAsync(SettingsSaved saved)
    {
        try
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));
            await _service.SaveSettingsAsync(saved.Settings, saved.ApiKey, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Application.Instance.AsyncInvoke(() =>
            {
                // The save may have been refused outright (a turn is running), so the dropdown is put
                // back in step with the provider that is actually in use.
                SyncSource(_service.Settings.Provider);
                AddMessage(new ServiceMessage("error", $"Настройки: {exception.Message}", "ConnectionFailed"));
            });
        }
    }

    private async Task SendOrStopAsync()
    {
        if (_busy)
        {
            _service.Stop();
            return;
        }

        RhinoDoc? document = RhinoDoc.ActiveDoc;
        string task = _prompt.Text.Trim();
        if (document is null || string.IsNullOrWhiteSpace(task))
        {
            return;
        }

        string? model = SelectedModelId();
        string? effort = SelectedEffortId();
        if (model is null || effort is null)
        {
            AddMessage(new ServiceMessage("error", "Сначала подключите провайдера и выберите модель в настройках.", "ConnectionRequired"));
            return;
        }

        TargetHost host = _target.SelectedIndex == 1 ? TargetHost.Grasshopper : TargetHost.Rhino;
        _prompt.Text = string.Empty;
        await _service.SendAsync(document, task, host, model, effort);
    }

    private async Task SendOrStopSafeAsync()
    {
        try
        {
            await SendOrStopAsync();
        }
        catch (Exception exception)
        {
            AddMessage(new ServiceMessage(
                "error",
                $"RhiGhAI безопасно остановил задачу: {exception.Message}",
                "UnhandledUiAction"));
        }
    }

    /// <summary>
    /// Reads the stored transcript off the UI thread. It takes a machine-wide mutex and walks up to
    /// 500 events, which is a visible pause on the frame that opens the dock.
    /// </summary>
    private async Task RestoreTranscriptSafeAsync(string? documentPath)
    {
        IReadOnlyList<ServiceMessage> restored;
        try
        {
            restored = await Task.Run(() => _service.RestoreTranscript(documentPath)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            restored = Array.Empty<ServiceMessage>();
        }

        Application.Instance.AsyncInvoke(() =>
        {
            if (restored.Count == 0)
            {
                AddMessage(new ServiceMessage("system", "Опишите задачу обычным языком. RhiGhAI покажет проверенный план перед выполнением."));
                return;
            }

            foreach (ServiceMessage message in restored)
            {
                AddMessage(message);
            }
        });
    }

    private async Task RefreshConnectionSafeAsync()
    {
        try
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));
            await _service.RefreshAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Application.Instance.AsyncInvoke(() =>
                AddMessage(new ServiceMessage("error", $"Подключение: {exception.Message}", "ConnectionFailed")));
        }
    }

    private void ServiceOnMessage(object? sender, ServiceMessage message) => Application.Instance.AsyncInvoke(() => AddMessage(message));
    private void ServiceOnConnectionChanged(object? sender, ConnectionSnapshot connection) => Application.Instance.AsyncInvoke(() => ApplyConnection(connection));
    private void ServiceOnBusyChanged(object? sender, bool busy) => Application.Instance.AsyncInvoke(() => SetBusy(busy));

    private void ApplyConnection(ConnectionSnapshot connection)
    {
        _connection = connection;
        _models = connection.Models;
        _model.Items.Clear();
        foreach (ProviderModel model in _models)
        {
            _model.Items.Add(model.DisplayName);
        }

        int selected = _models.ToList().FindIndex(item => item.Id == _service.Settings.ModelId);
        if (selected < 0)
        {
            selected = _models.ToList().FindIndex(item => item.IsDefault);
        }

        _model.SelectedIndex = selected >= 0 ? selected : (_models.Count > 0 ? 0 : -1);
        UpdateEfforts();
        UpdateFooter();
        SyncSource(connection.Provider);

        if (connection.Provider == ProviderKind.OpenAiCompatible && !connection.Ready && !_announcedProviderSetup)
        {
            _announcedProviderSetup = true;
            AddMessage(new ServiceMessage("system", $"{connection.StatusText} Откройте «Настройки» и заполните адрес API, ключ и модель."));
        }
        else if (connection.Ready)
        {
            _announcedProviderSetup = false;
        }

        if (connection.Runtime is { } runtime && _announcedRuntimeState != runtime.State)
        {
            _announcedRuntimeState = runtime.State;
            if (runtime.State is RuntimeState.Missing or RuntimeState.Invalid)
            {
                AddMessage(new ServiceMessage("system", "Codex runtime не найден. Откройте «Настройки»: подключите Codex или переключитесь на API-провайдера с ключом."));
            }
        }

        bool loginRequired = connection.Provider == ProviderKind.Codex && connection.Runtime?.State == RuntimeState.Ready && !connection.Ready;
        if (loginRequired && !_announcedLoginRequired)
        {
            _announcedLoginRequired = true;
            AddMessage(new ServiceMessage("system", "Codex готов. В «Настройках» нажмите «Войти в ChatGPT», завершите вход в браузере и обновите состояние."));
        }
        else if (!loginRequired)
        {
            _announcedLoginRequired = false;
        }
    }

    private void UpdateFooter()
    {
        string source = _connection.Provider == ProviderKind.Codex ? "CODEX" : "API";
        List<string> parts = [$"{source} · {_connection.AccountText}"];
        if (_compact)
        {
            _footer.Text = parts[0];
            return;
        }

        if (_connection.UsageText is { Length: > 0 } usage)
        {
            parts.Add($"USAGE {usage}");
        }

        parts.Add($"MODELS {_connection.Models.Count}");
        parts.Add($"v{ProductInfo.Version}");
        _footer.Text = string.Join("   ·   ", parts);
    }

    private void UpdateEfforts()
    {
        _effort.Items.Clear();
        if (_model.SelectedIndex < 0 || _model.SelectedIndex >= _models.Count)
        {
            _effort.SelectedIndex = -1;
            return;
        }

        ProviderModel model = _models[_model.SelectedIndex];
        foreach (string effort in model.Efforts)
        {
            _effort.Items.Add(effort);
        }

        int selected = model.Efforts.ToList().FindIndex(item => item == _service.Settings.EffortId);
        if (selected < 0)
        {
            selected = model.Efforts.ToList().FindIndex(item => item == model.DefaultEffort);
        }

        _effort.SelectedIndex = selected >= 0 ? selected : (model.Efforts.Count > 0 ? 0 : -1);
    }

    private void AddMessage(ServiceMessage message)
    {
        Control card = message.Kind switch
        {
            "user" => UserCard(message.Text),
            "progress" => RhiGhAIStyles.StepRow(message.Text, RhiGhAIStyles.Orange),
            "code" => CodeCard(message),
            "result" => MarkCard(RhiGhAIStyles.Lime, "✓", "Готово", message.Text, RhiGhAIStyles.Ink),
            "error" => MarkCard(RhiGhAIStyles.Red, "!", message.Code ?? "Ошибка", message.Text, RhiGhAIStyles.White),
            _ => NoteCard(message.Text)
        };

        _messages.Items.Add(new StackLayoutItem(card, HorizontalAlignment.Stretch));
        Application.Instance.AsyncInvoke(() => _messageScroll.ScrollPosition = new Point(0, int.MaxValue));
    }

    private static Control UserCard(string text) => new Panel
    {
        BackgroundColor = RhiGhAIStyles.Dark,
        Padding = new Padding(14, 11),
        Content = new StackLayout
        {
            Spacing = 5,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                RhiGhAIStyles.Micro("Вы", RhiGhAIStyles.Coral),
                new Label
                {
                    Text = text,
                    Wrap = WrapMode.Word,
                    Font = RhiGhAIStyles.Ui(10.5f),
                    TextColor = RhiGhAIStyles.White
                }
            }
        }
    };

    private static Control NoteCard(string text) => new Panel
    {
        Padding = new Padding(2, 0),
        Content = new Label
        {
            Text = text,
            Wrap = WrapMode.Word,
            Font = RhiGhAIStyles.Ui(10),
            TextColor = RhiGhAIStyles.Muted
        }
    };

    private static Control MarkCard(Color fill, string glyph, string title, string text, Color glyphColor)
    {
        StackLayout body = new()
        {
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = title, Font = RhiGhAIStyles.UiBold(10.5f), TextColor = RhiGhAIStyles.Ink },
                new Label { Text = text, Wrap = WrapMode.Word, Font = RhiGhAIStyles.Ui(10), TextColor = RhiGhAIStyles.Muted }
            }
        };
        // A bare cell stretches its content; the stack keeps the mark a 22 px square at the top.
        StackLayout markHolder = new() { Items = { new StackLayoutItem(RhiGhAIStyles.Mark(fill, glyph, glyphColor), false) } };
        TableLayout row = new()
        {
            Spacing = new Size(12, 0),
            Rows = { new TableRow(new TableCell(markHolder), new TableCell(body, true)) }
        };
        return RhiGhAIStyles.Bordered(row, RhiGhAIStyles.Line, RhiGhAIStyles.White, new Padding(12));
    }

    private static Control CodeCard(ServiceMessage message)
    {
        string code = message.Code ?? string.Empty;
        int lines = code.Count(character => character == '\n') + 1;
        RhiGhAIButton copy = RhiGhAIStyles.Button("КОПИРОВАТЬ", BrandButtonKind.Ghost, 110);
        copy.Height = 26;
        copy.Click += (_, _) => Clipboard.Instance.Text = code;

        TextArea codeArea = new()
        {
            Text = code,
            ReadOnly = true,
            Wrap = false,
            Height = Math.Clamp(28 + (lines * 15), 60, 190),
            Font = RhiGhAIStyles.Mono(),
            BackgroundColor = RhiGhAIStyles.Input,
            TextColor = RhiGhAIStyles.Ink
        };

        TableLayout head = new()
        {
            Spacing = new Size(8, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(RhiGhAIStyles.Micro($"План · {lines} строк", RhiGhAIStyles.Muted), true),
                    new TableCell(copy))
            }
        };

        StackLayout content = new()
        {
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Panel { Padding = new Padding(12, 9), Content = head },
                new Panel { BackgroundColor = RhiGhAIStyles.Line, Height = 1 },
                new Panel { Padding = new Padding(1), Content = codeArea },
                new Panel { Padding = new Padding(12, 9), Content = new Label
                {
                    Text = message.Text,
                    Wrap = WrapMode.Word,
                    Font = RhiGhAIStyles.Ui(10),
                    TextColor = RhiGhAIStyles.Muted
                } }
            }
        };

        return RhiGhAIStyles.Bordered(content, RhiGhAIStyles.Line, RhiGhAIStyles.White, new Padding(0));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Text = busy ? "ОСТАНОВИТЬ" : "ОТПРАВИТЬ";
        _send.FillColor = busy ? RhiGhAIStyles.Red : RhiGhAIStyles.Pink;
        _send.ForegroundColor = busy ? RhiGhAIStyles.White : RhiGhAIStyles.Ink;
        _send.BorderColor = busy ? RhiGhAIStyles.Red : RhiGhAIStyles.Pink;
        _send.Invalidate();
        _model.Enabled = !busy;
        _effort.Enabled = !busy;
        _target.Enabled = !busy;
        _source.Enabled = !busy;
        _settings.Enabled = !busy;
        SetAgentState(busy ? "агент работает" : "готов к задаче", busy ? RhiGhAIStyles.Orange : RhiGhAIStyles.Muted);
    }

    private void SetAgentState(string text, Color color)
    {
        _agentState.Text = $"● {text}".ToUpperInvariant();
        _agentState.TextColor = color;
    }

    private static void SetButton(RhiGhAIButton button, string text, int width)
    {
        button.Text = text;
        button.MinimumSize = new Size(width, 32);
        button.Size = new Size(width, 32);
        button.Invalidate();
    }

    private void SelectionChanged(object? sender, RhinoObjectSelectionEventArgs args) => Application.Instance.AsyncInvoke(UpdateSelection);
    private void SelectionCleared(object? sender, RhinoDeselectAllObjectsEventArgs args) => Application.Instance.AsyncInvoke(UpdateSelection);
    private void ActiveDocumentChanged(object? sender, DocumentEventArgs args) => Application.Instance.AsyncInvoke(UpdateSelection);

    protected override void OnUnLoad(EventArgs e)
    {
        _service.Message -= ServiceOnMessage;
        _service.ConnectionChanged -= ServiceOnConnectionChanged;
        _service.BusyChanged -= ServiceOnBusyChanged;
        RhinoDoc.SelectObjects -= SelectionChanged;
        RhinoDoc.DeselectObjects -= SelectionChanged;
        RhinoDoc.DeselectAllObjects -= SelectionCleared;
        RhinoDoc.ActiveDocumentChanged -= ActiveDocumentChanged;
        base.OnUnLoad(e);
    }

    private void UpdateSelection()
    {
        RhinoDoc? document = RhinoDoc.ActiveDoc;
        if (document is null)
        {
            _selection.Text = "ДОКУМЕНТ НЕ ОТКРЫТ";
            return;
        }

        RhinoObject[] selected = document.Objects.GetSelectedObjects(false, false).ToArray();
        if (selected.Length == 0)
        {
            _selection.Text = $"НИЧЕГО НЕ ВЫДЕЛЕНО · {document.ModelUnitSystem}".ToUpperInvariant();
            return;
        }

        string warning = selected.Length > 100 ? " · ЛИМИТ 100" : string.Empty;
        if (_compact)
        {
            _selection.Text = $"ВЫДЕЛЕНО {selected.Length}{warning}";
            return;
        }

        string types = string.Join(", ", selected.GroupBy(item => item.ObjectType).Select(group => $"{group.Key} × {group.Count()}"));
        _selection.Text = $"ВЫДЕЛЕНО {selected.Length} · {types} · {document.ModelUnitSystem}{warning}".ToUpperInvariant();
    }

    private string? SelectedModelId() => _model.SelectedIndex >= 0 && _model.SelectedIndex < _models.Count ? _models[_model.SelectedIndex].Id : null;

    private string? SelectedEffortId() =>
        _model.SelectedIndex >= 0 &&
        _model.SelectedIndex < _models.Count &&
        _effort.SelectedIndex >= 0 &&
        _effort.SelectedIndex < _models[_model.SelectedIndex].Efforts.Count
            ? _models[_model.SelectedIndex].Efforts[_effort.SelectedIndex]
            : null;
}
