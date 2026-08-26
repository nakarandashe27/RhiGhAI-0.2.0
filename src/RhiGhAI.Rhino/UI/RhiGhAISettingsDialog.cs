using Eto.Drawing;
using Eto.Forms;
using RhiGhAI.Core;
using RhiGhAI.Core.Codex;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Persistence;
using RhiGhAI.Core.Providers;
using RhiGhAI.Rhino.Services;

namespace RhiGhAI.Rhino.UI;

/// <summary>
/// Settings live in their own window: a docked Rhino panel is too narrow to hold connection
/// state, provider fields and execution limits without being dragged open.
/// </summary>
internal sealed class RhiGhAISettingsDialog : Dialog
{
    private readonly RhiGhAIService _service;
    private readonly Label _status = new() { Font = RhiGhAIStyles.Ui(10), TextColor = RhiGhAIStyles.White, Wrap = WrapMode.Word };
    private readonly Label _activity = RhiGhAIStyles.MachineLabel("Проверяю", RhiGhAIStyles.Lime);
    private readonly RhiGhAIButton _refresh = RhiGhAIStyles.Button("ОБНОВИТЬ", BrandButtonKind.Ghost, 104);
    private readonly RhiGhAIButton _install = RhiGhAIStyles.Button("ПОДКЛЮЧИТЬ CODEX", BrandButtonKind.Orange, 158);
    private readonly RhiGhAIButton _login = RhiGhAIStyles.Button("ВОЙТИ В CHATGPT", BrandButtonKind.Blue, 148);
    private readonly RhiGhAIButton _logout = RhiGhAIStyles.Button("ВЫЙТИ", BrandButtonKind.Danger, 84);
    private readonly DropDown _provider = new() { Font = RhiGhAIStyles.Ui(10), Height = 30 };
    private readonly TextBox _endpoint = new() { Font = RhiGhAIStyles.Ui(10), Height = 30, PlaceholderText = "https://api.openai.com/v1" };
    private readonly PasswordBox _apiKey = new() { Font = RhiGhAIStyles.Ui(10), Height = 30 };
    private readonly RhiGhAIButton _clearKey = RhiGhAIStyles.Button("УДАЛИТЬ", BrandButtonKind.Danger, 76);
    private readonly TextBox _model = new() { Font = RhiGhAIStyles.Ui(10), Height = 30, PlaceholderText = "gpt-5.1 · deepseek-chat · anthropic/claude-sonnet-4.5" };
    private readonly NumericStepper _retries;
    private readonly NumericStepper _timeout;
    private readonly string? _selectedModel;
    private readonly string? _selectedEffort;
    private bool _actionRunning;
    private bool _keyCleared;

    public RhiGhAISettingsDialog(RhiGhAIService service, string? selectedModel, string? selectedEffort)
    {
        _service = service;
        _selectedModel = selectedModel;
        _selectedEffort = selectedEffort;
        _retries = Stepper(1, 5, service.Settings.RetryMax);
        _timeout = Stepper(30, 600, service.Settings.TimeoutSeconds);

        Title = $"RhiGhAI · настройки · v{ProductInfo.Version}";
        BackgroundColor = RhiGhAIStyles.Section;
        MinimumSize = new Size(520, 460);
        ClientSize = new Size(560, 560);
        Resizable = true;
        Content = BuildContent();

        _provider.Items.Add("Codex · вход ChatGPT");
        _provider.Items.Add("API-ключ · OpenAI-совместимый");
        _provider.SelectedIndex = service.Settings.Provider == ProviderKind.Codex ? 0 : 1;
        _provider.SelectedIndexChanged += (_, _) => UpdateStatus();
        _endpoint.Text = service.Settings.Endpoint ?? string.Empty;
        _model.Text = service.Settings.ModelId ?? string.Empty;
        _apiKey.ToolTip = "Ключ шифруется DPAPI для этого пользователя Windows. Пустое поле оставляет сохранённый ключ.";
        _clearKey.ToolTip = "Удалить сохранённый ключ с диска при сохранении настроек.";
        _clearKey.Click += (_, _) =>
        {
            // An empty field means "keep what is stored", so without this the delete branch of
            // SecretStore was unreachable from the interface.
            _keyCleared = true;
            _apiKey.Text = string.Empty;
            _status.Text = "Сохранённый ключ будет удалён при сохранении настроек.";
            UpdateStatus();
        };

        _refresh.Click += (_, _) => _ = RunActionAsync("Проверяю подключение", () => WithTimeoutAsync(_service.RefreshAsync));
        _install.Click += (_, _) =>
        {
            Progress<double> progress = new(value => Application.Instance.AsyncInvoke(() =>
            {
                _activity.Text = $"[ ПОДГОТОВКА CODEX · {value:P0} ]";
                _status.Text = "Копирую подписанный runtime из Codex Desktop/CLI. Сеть не используется.";
            }));
            _ = RunActionAsync("Подключение Codex", () => WithTimeoutAsync(token => _service.PrepareRuntimeAsync(progress, token), 300));
        };
        _login.Click += (_, _) => _ = RunActionAsync("Открываю вход ChatGPT", async () =>
        {
            _ = await WithTimeoutAsync(_service.LoginAsync).ConfigureAwait(false);
            Application.Instance.AsyncInvoke(() => _status.Text = "Завершите вход в браузере, затем нажмите «Обновить».");
        });
        _logout.Click += (_, _) => _ = RunActionAsync("Выход из аккаунта", () => WithTimeoutAsync(_service.LogoutAsync));

        KeyDown += (_, args) =>
        {
            if (args.Key == Keys.Escape)
            {
                args.Handled = true;
                Close();
            }
        };

        UpdateStatus();
    }

    private Control BuildContent()
    {
        RhiGhAIButton save = RhiGhAIStyles.Button("СОХРАНИТЬ И ЗАКРЫТЬ", BrandButtonKind.Pink, 200);
        save.Click += (_, _) => Save();

        StackLayout connection = new()
        {
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                _activity,
                _status,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items = { _refresh, _install, _login, _logout }
                }
            }
        };

        StackLayout body = new()
        {
            Padding = new Padding(20, 18),
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                RhiGhAIStyles.MachineLabel("01 · Подключение", RhiGhAIStyles.Orange),
                RhiGhAIStyles.Surface(connection, new Padding(14), RhiGhAIStyles.Dark),
                new Panel { Height = 6 },
                RhiGhAIStyles.MachineLabel("02 · Провайдер модели", RhiGhAIStyles.Orange),
                RhiGhAIStyles.Bordered(ProviderFields(), RhiGhAIStyles.Line, RhiGhAIStyles.White, new Padding(14)),
                new Panel { Height = 6 },
                RhiGhAIStyles.MachineLabel("03 · Выполнение", RhiGhAIStyles.Orange),
                RhiGhAIStyles.Bordered(ExecutionFields(), RhiGhAIStyles.Line, RhiGhAIStyles.White, new Padding(14))
            }
        };

        Panel foot = new()
        {
            BackgroundColor = RhiGhAIStyles.Section,
            Padding = new Padding(20, 12),
            Content = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Items = { save }
            }
        };

        return new TableLayout
        {
            Spacing = Size.Empty,
            Rows =
            {
                new TableRow(new TableCell(new Scrollable
                {
                    Content = body,
                    Border = BorderType.None,
                    BackgroundColor = RhiGhAIStyles.Section
                }, true)) { ScaleHeight = true },
                new TableRow(foot)
            }
        };
    }

    private Control ProviderFields()
    {
        string keyLabel = _service.ApiKeyHint is { } hint ? $"API-ключ · {hint} сохранён" : "API-ключ";
        StackLayout keyRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(_apiKey, true), _clearKey }
        };

        return new TableLayout
        {
            Spacing = new Size(14, 10),
            Rows =
            {
                Field("Источник модели", _provider),
                Field("Адрес API", _endpoint),
                Field(keyLabel, keyRow),
                Field("Модель", _model),
                new TableRow(
                    new TableCell(new Label
                    {
                        Text = "OpenAI /v1 · OpenRouter /api/v1 · DeepSeek /v1 · Anthropic /v1 · Ollama localhost:11434/v1.\n"
                            + "Ключ хранится зашифрованным для этого пользователя Windows.",
                        Wrap = WrapMode.Word,
                        Font = RhiGhAIStyles.Ui(8.5f),
                        TextColor = RhiGhAIStyles.Muted
                    }, true),
                    new TableCell(null))
            }
        };
    }

    private Control ExecutionFields() => new TableLayout
    {
        Spacing = new Size(14, 10),
        Rows =
        {
            Field("Попыток самоисправления", _retries),
            Field("Тайм-аут задачи, секунд", _timeout)
        }
    };

    private static TableRow Field(string label, Control control) => new(
        new TableCell(new Label
        {
            Text = label,
            Font = RhiGhAIStyles.UiBold(10),
            TextColor = RhiGhAIStyles.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        }, true),
        new TableCell(new Panel { Width = 260, Content = control }));

    private static NumericStepper Stepper(int minimum, int maximum, int value) => new()
    {
        MinValue = minimum,
        MaxValue = maximum,
        Value = value,
        DecimalPlaces = 0,
        Font = RhiGhAIStyles.Ui(10),
        Height = 30
    };

    private bool CodexSelected() => _provider.SelectedIndex == 0;

    private void UpdateStatus()
    {
        ConnectionSnapshot connection = _service.Connection;
        _status.Text = $"{connection.StatusText}\nДоступных моделей: {connection.Models.Count}";
        _activity.Text = connection.Ready
            ? "[ ПОДКЛЮЧЕНО ]"
            : connection.Provider == ProviderKind.Codex
                ? connection.Runtime?.State == RuntimeState.Ready ? "[ НУЖЕН ВХОД ]" : "[ CODEX НЕ НАЙДЕН ]"
                : "[ НУЖЕН КЛЮЧ ИЛИ АДРЕС ]";
        _activity.TextColor = connection.Ready ? RhiGhAIStyles.Lime : RhiGhAIStyles.Orange;

        bool codex = CodexSelected();
        _endpoint.Enabled = !codex;
        _apiKey.Enabled = !codex;
        _model.Enabled = !codex;
        _clearKey.Enabled = !codex && !_keyCleared && _service.ApiKeyHint is not null;
        if (_actionRunning)
        {
            return;
        }

        _refresh.Enabled = true;
        _install.Enabled = codex && connection.Runtime?.State != RuntimeState.Ready;
        _login.Enabled = codex && connection.Runtime?.State == RuntimeState.Ready && !connection.Ready;
        _logout.Enabled = codex && connection.Ready;
    }

    private void SetActionRunning(bool running, string? message = null)
    {
        _actionRunning = running;
        foreach (RhiGhAIButton button in new[] { _refresh, _install, _login, _logout })
        {
            button.Enabled = !running;
        }

        if (message is not null)
        {
            _activity.Text = $"[ {message.ToUpperInvariant()} ]";
            _activity.TextColor = RhiGhAIStyles.Orange;
        }
    }

    private async Task RunActionAsync(string pending, Func<Task> action)
    {
        if (_actionRunning)
        {
            return;
        }

        SetActionRunning(true, pending);
        try
        {
            await action().ConfigureAwait(false);
            Application.Instance.AsyncInvoke(() =>
            {
                SetActionRunning(false);
                UpdateStatus();
            });
        }
        catch (Exception exception)
        {
            Application.Instance.AsyncInvoke(() =>
            {
                SetActionRunning(false);
                // SetActionRunning turns all four buttons back on unconditionally; only UpdateStatus
                // knows that "Войти в ChatGPT" and "Выйти" are meaningless in the current state.
                UpdateStatus();
                _activity.Text = "[ ОШИБКА ]";
                _activity.TextColor = RhiGhAIStyles.Red;
                _status.Text = exception.Message;
            });
        }
    }

    private void Save()
    {
        RhiGhAISettings next;
        // null keeps the stored key, an empty string deletes it, anything else replaces it.
        string? key = _apiKey.Text.Length > 0 ? _apiKey.Text : _keyCleared ? string.Empty : null;
        try
        {
            string typedModel = _model.Text.Trim();
            string typedEndpoint = _endpoint.Text.Trim();
            next = new RhiGhAISettings(
                1,
                (int)_retries.Value,
                (int)_timeout.Value,
                _service.Settings.DefaultTarget,
                typedModel.Length > 0 ? typedModel : _selectedModel,
                _selectedEffort,
                CodexSelected() ? ProviderKind.Codex : ProviderKind.OpenAiCompatible,
                typedEndpoint.Length > 0 ? typedEndpoint : null).Validate();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            _activity.Text = "[ НЕ СОХРАНЕНО ]";
            _activity.TextColor = RhiGhAIStyles.Red;
            _status.Text = exception.Message;
            return;
        }

        Close();
        Saved?.Invoke(this, new SettingsSaved(next, key));
    }

    public event EventHandler<SettingsSaved>? Saved;

    private static async Task WithTimeoutAsync(Func<CancellationToken, Task> action, int seconds = 120)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(seconds));
        await action(cancellation.Token).ConfigureAwait(false);
    }

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, int seconds = 120)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(seconds));
        return await action(cancellation.Token).ConfigureAwait(false);
    }
}

internal sealed record SettingsSaved(RhiGhAISettings Settings, string? ApiKey);
