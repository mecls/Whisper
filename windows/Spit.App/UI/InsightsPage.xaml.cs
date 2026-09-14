using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Spit.Core;

namespace Spit.App;

/// Four cards, numbers only (Mac `InsightsView`). Which state to draw, which cell a day lands in and how a
/// number reads all come from `InsightsPresentation`, `HeatmapGrid` and `InsightsFormat`; this is layout.
/// Cached numbers render immediately because `InsightsModel` loads its cache before the first render.
public partial class InsightsPage : UserControl, IDisposable
{
    private const double Cell = 11;
    private const double CellGap = 3;

    private readonly AppModel model;
    private readonly InsightsModel insights;
    private bool disposed;

    public InsightsPage(AppModel model, InsightsModel insights)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(insights);
        this.model = model;
        this.insights = insights;
        InitializeComponent();

        insights.PresentationChanged += OnPresentationChanged;
        // Like the Mac's `.task { model.refresh() }`: one refresh each time the page appears, no polling.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Render();
                _ = model.RequestRefreshInsights();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Render();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        insights.PresentationChanged -= OnPresentationChanged;
    }

    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) Render();
        else Dispatcher.BeginInvoke(DispatcherPriority.Normal, Render);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = model.RequestRefreshInsights();

    private void OnOpenServerSettings(object sender, RoutedEventArgs e) => _ = model.RequestOpenSettings(SettingsSection.Server);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = model.RequestRefreshInsights();
        }
    }

    private void Render()
    {
        var presentation = insights.Presentation;
        RenderNotice(presentation.Notice);
        var content = presentation.Content;
        RenderTotalWords(content);
        RenderWpm(content);
        RenderStreak(content);
        RenderApps(content);
    }

    private void RenderNotice(InsightsNotice notice)
    {
        var text = InsightsPresentation.NoticeText(notice);
        NoticeBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = text ?? "";
        NoticeLinkHost.Visibility = notice is InsightsNotice.Unauthorized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTotalWords(InsightsContent content)
    {
        TotalWordsBody.Children.Clear();
        switch (content)
        {
            case InsightsContent.Loading:
                AddFigure(TotalWordsBody, null, Strings.CardTotalWordsCaption, loading: true);
                break;
            case InsightsContent.Empty:
                AddFigure(TotalWordsBody, null, Strings.CardTotalWordsCaption, loading: false);
                TotalWordsBody.Children.Add(EmptyHint());
                break;
            case InsightsContent.Populated p:
                AddFigure(TotalWordsBody, InsightsFormat.Number(p.Insights.Totals.Words), Strings.CardTotalWordsCaption, loading: false);
                break;
        }
    }

    private void RenderWpm(InsightsContent content)
    {
        WpmBody.Children.Clear();
        switch (content)
        {
            case InsightsContent.Loading:
                AddFigure(WpmBody, null, Strings.CardWpmCaption, loading: true);
                break;
            case InsightsContent.Empty:
                AddFigure(WpmBody, null, Strings.CardWpmCaption, loading: false);
                WpmBody.Children.Add(EmptyHint());
                break;
            case InsightsContent.Populated p:
                // A null wpm is under a minute of total audio: a dash, not a made-up rate.
                var wpm = p.Insights.Totals.Wpm is { } n ? InsightsFormat.Number(n) : null;
                AddFigure(WpmBody, wpm, Strings.CardWpmCaption, loading: false);
                break;
        }
    }

    private void RenderStreak(InsightsContent content)
    {
        StreakBody.Children.Clear();
        switch (content)
        {
            case InsightsContent.Loading:
                AddFigure(StreakBody, null, "", loading: true);
                break;
            case InsightsContent.Empty:
                StreakBody.Children.Add(Headline(Strings.DayStreak(0)));
                StreakBody.Children.Add(Heatmap(HeatmapGrid.Empty()));
                StreakBody.Children.Add(EmptyHint());
                break;
            case InsightsContent.Populated p:
                StreakBody.Children.Add(Headline(Strings.DayStreak(p.Insights.Streak.Current)));
                StreakBody.Children.Add(Heatmap(HeatmapGrid.Build(p.Insights.Days)));
                StreakBody.Children.Add(Caption(Strings.LongestStreak(p.Insights.Streak.Longest)));
                break;
        }
    }

    private void RenderApps(InsightsContent content)
    {
        AppsBody.Children.Clear();
        switch (content)
        {
            case InsightsContent.Loading:
                AddFigure(AppsBody, null, "", loading: true);
                break;
            case InsightsContent.Empty:
                AppsBody.Children.Add(Headline(Strings.NoValue));
                AppsBody.Children.Add(EmptyHint());
                break;
            case InsightsContent.Populated p:
                foreach (var app in p.Insights.Apps) AppsBody.Children.Add(AppRow(app));
                break;
        }
    }

    // MARK: - Pieces

    /// The number a card is built around, or its loading placeholder. `—` rather than `0` for empty:
    /// zero is a measurement, and nothing has been measured yet.
    private static void AddFigure(Panel panel, string? value, string caption, bool loading)
    {
        if (loading)
        {
            var placeholder = new Border
            {
                Width = 120,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            placeholder.SetResourceReference(Border.BackgroundProperty, "Spit.Placeholder");
            panel.Children.Add(placeholder);
        }
        else
        {
            var figure = new TextBlock { Text = value ?? Strings.NoValue };
            figure.SetResourceReference(StyleProperty, "Spit.Figure");
            panel.Children.Add(figure);
        }
        if (caption.Length > 0)
        {
            var c = Caption(caption);
            c.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(c);
        }
    }

    private static TextBlock Headline(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "Spit.Headline");
        return t;
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "Spit.Caption");
        return t;
    }

    private static TextBlock EmptyHint()
    {
        var t = new TextBlock { Text = Strings.InsightsEmpty, Margin = new Thickness(0, 8, 0, 0) };
        t.SetResourceReference(StyleProperty, "Spit.Hint");
        return t;
    }

    /// Fixed cell size, scrolling horizontally with the newest week pinned right: a heatmap that drops
    /// weeks at narrow widths would change its own story when the window is resized.
    private static FrameworkElement Heatmap(HeatmapGrid grid)
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        for (var w = 0; w < grid.Weeks.Count; w++)
        {
            var week = grid.Weeks[w];
            var column = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, w == grid.Weeks.Count - 1 ? 0 : CellGap, 0),
            };
            for (var row = 0; row < 7; row++)
            {
                var cell = row < week.Count ? week[row] : null;
                var square = new Rectangle
                {
                    Width = Cell,
                    Height = Cell,
                    RadiusX = 2,
                    RadiusY = 2,
                    Margin = new Thickness(0, 0, 0, row == 6 ? 0 : CellGap),
                };
                if (cell is null) square.Fill = Brushes.Transparent;
                else square.SetResourceReference(Shape.FillProperty, cell.Level switch
                {
                    1 => "Spit.Heat1",
                    2 => "Spit.Heat2",
                    3 => "Spit.Heat3",
                    _ => "Spit.Heat0",
                });
                column.Children.Add(square);
            }
            columns.Children.Add(column);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = columns,
            Margin = new Thickness(0, 12, 0, 12),
        };
        // The newest week is the one the user came to see, so it must be on screen without scrolling.
        scroller.Loaded += (_, _) => scroller.ScrollToRightEnd();
        scroller.SizeChanged += (_, _) => scroller.ScrollToRightEnd();
        return scroller;
    }

    private static FrameworkElement AppRow(InsightsAppShare app)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var label = new TextBlock
        {
            Text = app.Label,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Spit.Text");
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        // The share bar: a star-sized split of the column, at least 2 px so a 0 % app is still a mark.
        var share = Math.Clamp(app.Share, 0, 100);
        var track = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - share, GridUnitType.Star) });
        var bar = new Border { Height = 8, MinWidth = 2, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        bar.SetResourceReference(Border.BackgroundProperty, "Spit.Accent");
        if (share > 0) bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        track.Children.Add(bar);
        Grid.SetColumn(track, 2);
        row.Children.Add(track);

        var percent = new TextBlock
        {
            Text = $"{app.Share}%",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        percent.SetResourceReference(TextBlock.ForegroundProperty, "Spit.TextSecondary");
        Grid.SetColumn(percent, 3);
        row.Children.Add(percent);
        return row;
    }
}
