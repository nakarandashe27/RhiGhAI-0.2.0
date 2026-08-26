using Eto.Drawing;
using Eto.Forms;

namespace RhiGhAI.Rhino.UI;

internal enum BrandButtonKind
{
    Dark,
    Orange,
    Pink,
    Blue,
    Ghost,
    Danger
}

internal static class RhiGhAIStyles
{
    public static readonly Color Dark = Color.FromArgb(38, 38, 38);
    public static readonly Color BrandBlack = Color.FromArgb(22, 16, 16);
    public static readonly Color Ink = Color.FromArgb(22, 16, 16);
    public static readonly Color White = Colors.White;
    public static readonly Color Section = Color.FromArgb(247, 247, 247);
    public static readonly Color Input = Color.FromArgb(246, 246, 246);
    public static readonly Color Line = Color.FromArgb(218, 218, 218);
    public static readonly Color Blue = Color.FromArgb(77, 97, 244);
    public static readonly Color Red = Color.FromArgb(247, 76, 46);
    public static readonly Color Orange = Color.FromArgb(242, 141, 5);
    public static readonly Color Coral = Color.FromArgb(255, 133, 98);
    public static readonly Color Lime = Color.FromArgb(208, 233, 126);
    public static readonly Color Pink = Color.FromArgb(255, 182, 252);
    public static readonly Color Muted = Color.FromArgb(104, 104, 104);
    public static readonly Color Disabled = Color.FromArgb(232, 232, 232);

    // Advaken Sans and Inter Tight are not redistributed in the local MVP.
    // Windows-safe fallbacks preserve the display/body hierarchy from the brand brief.
    public static Font Display(float size) => new("Arial", size, FontStyle.Bold);
    public static Font Ui(float size = 10.5f) => new("Segoe UI", size);
    public static Font UiBold(float size = 10.5f) => new("Segoe UI", size, FontStyle.Bold);
    public static Font Mono(float size = 9.5f) => new("Consolas", size);

    public static RhiGhAIButton Button(string text, BrandButtonKind kind, int width = 112)
    {
        (Color fill, Color foreground, Color border) = kind switch
        {
            BrandButtonKind.Orange => (Orange, Ink, Orange),
            BrandButtonKind.Pink => (Pink, Ink, Pink),
            BrandButtonKind.Blue => (Blue, White, Blue),
            BrandButtonKind.Ghost => (Dark, White, Color.FromArgb(132, 132, 132)),
            BrandButtonKind.Danger => (Red, White, Red),
            _ => (Dark, White, Dark)
        };

        return new RhiGhAIButton(text, fill, foreground, border)
        {
            Size = new Size(width, 32),
            MinimumSize = new Size(width, 32)
        };
    }

    public static Label MachineLabel(string text, Color? color = null) => new()
    {
        Text = $"[ {text.ToUpperInvariant()} ]",
        TextColor = color ?? Muted,
        Font = Ui(9)
    };

    public static Label BodyLabel(string text, Color? color = null, bool bold = false) => new()
    {
        Text = text,
        TextColor = color ?? Ink,
        Font = bold ? UiBold(10.5f) : Ui(10.5f),
        Wrap = WrapMode.Word
    };

    public static Panel Surface(Control content, Padding? padding = null, Color? background = null) => new()
    {
        BackgroundColor = background ?? White,
        Padding = padding ?? new Padding(14),
        Content = content
    };

    /// <summary>Eto panels have no border property; a 1px coloured wrapper is the flat-design equivalent.</summary>
    public static Panel Bordered(Control content, Color? border = null, Color? fill = null, Padding? padding = null) => new()
    {
        BackgroundColor = border ?? Line,
        Padding = new Padding(1),
        Content = new Panel
        {
            BackgroundColor = fill ?? White,
            Padding = padding ?? new Padding(12),
            Content = content
        }
    };

    public static Label Micro(string text, Color? color = null) => new()
    {
        Text = text.ToUpperInvariant(),
        TextColor = color ?? Muted,
        Font = Mono(8.5f)
    };

    /// <summary>Progress line from the reference layout: a short accent rule, then a small mono caption.</summary>
    public static Control StepRow(string text, Color? accent = null) => new StackLayout
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
            new Panel { BackgroundColor = accent ?? Orange, Size = new Size(16, 2) },
            new StackLayoutItem(Micro(text, Muted), true)
        }
    };

    public static Panel Mark(Color fill, string glyph, Color? foreground = null) => new()
    {
        BackgroundColor = fill,
        Size = new Size(22, 22),
        Content = new Label
        {
            Text = glyph,
            TextColor = foreground ?? Ink,
            Font = UiBold(10),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
}

internal sealed class RhiGhAIButton : Drawable
{
    private bool _hovered;
    private bool _pressed;
    private string _text;

    public RhiGhAIButton(string text, Color fill, Color foreground, Color border)
    {
        _text = text;
        FillColor = fill;
        ForegroundColor = foreground;
        BorderColor = border;
        ButtonFont = RhiGhAIStyles.UiBold(10);
        Cursor = Cursors.Pointer;

        MouseEnter += (_, _) =>
        {
            _hovered = true;
            Invalidate();
        };
        MouseLeave += (_, _) =>
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
        };
        MouseDown += (_, args) =>
        {
            if (!Enabled || (args.Buttons & MouseButtons.Primary) == 0)
            {
                return;
            }

            Focus();
            _pressed = true;
            Invalidate();
        };
        MouseUp += (_, _) =>
        {
            bool click = Enabled && _pressed;
            _pressed = false;
            Invalidate();
            if (click)
            {
                Click?.Invoke(this, EventArgs.Empty);
            }
        };
        KeyDown += (_, args) =>
        {
            if (!Enabled || args.Key is not (Keys.Enter or Keys.Space))
            {
                return;
            }

            args.Handled = true;
            Click?.Invoke(this, EventArgs.Empty);
        };
        EnabledChanged += (_, _) => Invalidate();
    }

    public event EventHandler<EventArgs>? Click;

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            Invalidate();
        }
    }

    public Color FillColor { get; set; }
    public Color ForegroundColor { get; set; }
    public Color BorderColor { get; set; }
    public Font ButtonFont { get; set; }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        Color fill = !Enabled
            ? RhiGhAIStyles.Disabled
            : _pressed
                ? Shade(FillColor, 0.82f)
                : _hovered
                    ? Shade(FillColor, 0.92f)
                    : FillColor;
        Color foreground = Enabled ? ForegroundColor : RhiGhAIStyles.Muted;
        Color border = Enabled ? BorderColor : RhiGhAIStyles.Line;
        RectangleF bounds = new(0.5f, 0.5f, Math.Max(0, ClientSize.Width - 1f), Math.Max(0, ClientSize.Height - 1f));
        args.Graphics.FillRectangle(fill, bounds);
        args.Graphics.DrawRectangle(new Pen(border, 1), bounds);
        SizeF textSize = args.Graphics.MeasureString(ButtonFont, Text);
        args.Graphics.DrawText(
            ButtonFont,
            foreground,
            Math.Max(0, (ClientSize.Width - textSize.Width) / 2f),
            Math.Max(0, (ClientSize.Height - textSize.Height) / 2f),
            Text);
    }

    private static Color Shade(Color color, float factor) => Color.FromArgb(
        color.Ab,
        Math.Clamp((int)(color.Rb * factor), 0, 255),
        Math.Clamp((int)(color.Gb * factor), 0, 255),
        Math.Clamp((int)(color.Bb * factor), 0, 255));
}

internal sealed class RhiGhAIStairs : Drawable
{
    public RhiGhAIStairs(Color color)
    {
        Color = color;
        Size = new Size(30, 30);
        MinimumSize = Size;
    }

    public Color Color { get; }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.FillRectangle(Color, 0, 20, 10, 10);
        args.Graphics.FillRectangle(Color, 9, 10, 10, 20);
        args.Graphics.FillRectangle(Color, 18, 0, 10, 30);
    }
}
