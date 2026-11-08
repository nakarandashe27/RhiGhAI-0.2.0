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
    public static readonly Color Dark = Color.FromArgb(42, 45, 51);
    public static readonly Color BrandBlack = Color.FromArgb(29, 31, 35);
    public static readonly Color Ink = Color.FromArgb(29, 31, 35);
    public static readonly Color White = Colors.White;
    public static readonly Color Section = Color.FromArgb(247, 247, 247);
    public static readonly Color Input = Color.FromArgb(246, 246, 246);
    public static readonly Color Line = Color.FromArgb(218, 218, 218);
    public static readonly Color Blue = Color.FromArgb(0, 102, 204);
    public static readonly Color Red = Color.FromArgb(190, 43, 39);
    public static readonly Color Orange = Color.FromArgb(0, 102, 204);
    public static readonly Color Coral = Color.FromArgb(155, 194, 235);
    public static readonly Color Lime = Color.FromArgb(182, 219, 194);
    public static readonly Color Pink = Color.FromArgb(0, 102, 204);
    public static readonly Color Muted = Color.FromArgb(96, 100, 108);
    public static readonly Color Disabled = Color.FromArgb(232, 232, 232);

    // Advaken Sans and Inter Tight are not redistributed in the local MVP.
    // Windows-safe fallbacks preserve the display/body hierarchy from the brand brief.
    public static Font Display(float size) => new("Segoe UI", size, FontStyle.Bold);
    public static Font Ui(float size = 10.5f) => new("Segoe UI", size);
    public static Font UiBold(float size = 10.5f) => new("Segoe UI", size, FontStyle.Bold);
    public static Font Mono(float size = 9.5f) => new("Consolas", size);

    public static RhiGhAIButton Button(string text, BrandButtonKind kind, int width = 112)
    {
        (Color fill, Color foreground, Color border) = kind switch
        {
            BrandButtonKind.Orange => (Blue, White, Blue),
            BrandButtonKind.Pink => (Blue, White, Blue),
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
        Text = text,
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

    public static Panel Surface(Control content, Padding? padding = null, Color? background = null) => Round(new Panel()
    {
        BackgroundColor = background ?? White,
        Padding = padding ?? new Padding(14),
        Content = content
    });

    /// <summary>Eto panels have no border property; a 1px coloured wrapper is the flat-design equivalent.</summary>
    public static Panel Bordered(Control content, Color? border = null, Color? fill = null, Padding? padding = null) => Round(new Panel()
    {
        BackgroundColor = border ?? Line,
        Padding = new Padding(1),
        Content = Round(new Panel()
        {
            BackgroundColor = fill ?? White,
            Padding = padding ?? new Padding(12),
            Content = content
        }, 9)
    });

    public static Panel Round(Panel panel, double radius = 10)
    {
        panel.Load += (_, _) =>
        {
            if (panel.ControlObject is not System.Windows.FrameworkElement native) { return; }
            void Clip() => native.Clip = new System.Windows.Media.RectangleGeometry(
                new System.Windows.Rect(0, 0, native.ActualWidth, native.ActualHeight), radius, radius);
            native.SizeChanged += (_, _) => Clip();
            Clip();
        };
        return panel;
    }

    public static Label Micro(string text, Color? color = null) => new()
    {
        Text = text,
        TextColor = color ?? Muted,
        Font = Ui(9)
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

/// <summary>Native button retains Tab, Enter/Space and Windows UI Automation; only its template changes.</summary>
internal sealed class RhiGhAIButton : Button
{
    public RhiGhAIButton(string text, Color fill, Color foreground, Color border)
    {
        Text = text;
        FillColor = fill;
        ForegroundColor = foreground;
        BorderColor = border;
        Font = RhiGhAIStyles.UiBold(10);
        ToolTip = text;
        Load += (_, _) => Invalidate();
    }
    public Color FillColor { get; set; }
    public Color ForegroundColor { get; set; }
    public Color BorderColor { get; set; }
    public new void Invalidate()
    {
        if (ControlObject is not System.Windows.Controls.Button native) { return; }
        native.Template = (System.Windows.Controls.ControlTemplate)System.Windows.Markup.XamlReader.Parse(TemplateXaml);
        native.Background = Brush(FillColor);
        native.Foreground = Brush(ForegroundColor);
        native.BorderBrush = Brush(BorderColor);
        native.FocusVisualStyle = null;
        native.Padding = new System.Windows.Thickness(10, 3, 10, 3);
        System.Windows.Automation.AutomationProperties.SetName(native, ToolTip ?? Text);
        if (System.Windows.SystemParameters.HighContrast)
        {
            native.Background = System.Windows.SystemColors.ControlBrush;
            native.Foreground = System.Windows.SystemColors.ControlTextBrush;
            native.BorderBrush = System.Windows.SystemColors.ControlTextBrush;
        }
    }
    private static System.Windows.Media.SolidColorBrush Brush(Color color) => new(
        System.Windows.Media.Color.FromArgb((byte)color.Ab, (byte)color.Rb, (byte)color.Gb, (byte)color.Bb));
    private const string TemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Grid Background="Transparent">
            <Border x:Name="Surface" CornerRadius="8" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Margin="2">
              <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center"
                                VerticalAlignment="Center" RecognizesAccessKey="True"/>
            </Border>
            <Border x:Name="FocusRing" CornerRadius="10" BorderThickness="2"
                    BorderBrush="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}" Visibility="Collapsed"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.9"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.72"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="FocusRing" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;
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
