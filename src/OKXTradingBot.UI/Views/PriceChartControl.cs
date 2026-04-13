using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.UI.Views;

public enum ChartType { Line, Candle }

public class PriceChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<Candle>?> CandlesProperty =
        AvaloniaProperty.Register<PriceChartControl, IReadOnlyList<Candle>?>(nameof(Candles));

    public static readonly StyledProperty<ChartType> ChartTypeProperty =
        AvaloniaProperty.Register<PriceChartControl, ChartType>(nameof(ChartType), ChartType.Line);

    public IReadOnlyList<Candle>? Candles
    {
        get => GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    public ChartType ChartType
    {
        get => GetValue(ChartTypeProperty);
        set => SetValue(ChartTypeProperty, value);
    }

    // ── 줌 / 팬 상태 ──────────────────────────────────
    private int    _viewCount  = 80;   // 화면에 표시할 캔들 수
    private int    _viewOffset = 0;    // 오른쪽 끝에서 얼마나 과거로 이동했는지
    private double _dragStartX;
    private int    _dragStartOffset;
    private bool   _isDragging;

    static PriceChartControl()
    {
        AffectsRender<PriceChartControl>(CandlesProperty);
        AffectsRender<PriceChartControl>(ChartTypeProperty);
    }

    public PriceChartControl()
    {
        ClipToBounds = true;
        PointerWheelChanged += OnWheel;
        PointerPressed      += OnPressed;
        PointerMoved        += OnMoved;
        PointerReleased     += OnReleased;
    }


    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var candles = Candles;
        if (candles == null || candles.Count < 2) return;
        int delta = e.Delta.Y > 0 ? -5 : 5;
        _viewCount = Math.Clamp(_viewCount + delta, 10, candles.Count);
        ClampOffset(candles.Count);
        InvalidateVisual();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging      = true;
        _dragStartX      = e.GetPosition(this).X;
        _dragStartOffset = _viewOffset;
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var candles = Candles;
        if (candles == null) return;
        double dx        = e.GetPosition(this).X - _dragStartX;
        double pixPerBar = (Bounds.Width - 78) / (double)_viewCount;
        int    shift     = (int)(-dx / pixPerBar);
        _viewOffset = Math.Clamp(_dragStartOffset + shift, 0, Math.Max(0, candles.Count - _viewCount));
        InvalidateVisual();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private void ClampOffset(int total)
    {
        _viewOffset = Math.Clamp(_viewOffset, 0, Math.Max(0, total - _viewCount));
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        // 투명 배경 → 포인터 이벤트 수신 가능하게 함
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        var allCandles = Candles;
        bool isDark    = ActualThemeVariant == ThemeVariant.Dark;

        var gridColor     = isDark ? Color.FromArgb(35,  200, 200, 255) : Color.FromArgb(50, 100, 100, 180);
        var lineColor     = isDark ? Color.Parse("#00BFA5")  : Color.Parse("#00796B");
        var fillTop       = isDark ? Color.FromArgb(55,   0, 191, 165) : Color.FromArgb(40,  0, 121, 107);
        var fillBot       = Color.FromArgb(0, 0, 0, 0);
        var labelColor    = isDark ? Color.Parse("#8080B0")  : Color.Parse("#4C5080");
        var axisColor     = isDark ? Color.Parse("#3A3A5C")  : Color.Parse("#BEC4DC");
        var curPriceColor = isDark ? Color.Parse("#FFC107")  : Color.Parse("#BF4000");
        var bullColor     = isDark ? Color.Parse("#26A69A")  : Color.Parse("#00897B");
        var bearColor     = isDark ? Color.Parse("#EF5350")  : Color.Parse("#E53935");

        if (allCandles == null || allCandles.Count < 2)
        {
            var msg = new FormattedText("데이터 로딩 중...", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Sans-Serif"), 11, new SolidColorBrush(labelColor));
            ctx.DrawText(msg, new Point((w - msg.Width) / 2, (h - msg.Height) / 2));
            return;
        }

        // ── 뷰 슬라이싱 ──────────────────────────────────
        int total   = allCandles.Count;
        int count   = Math.Min(_viewCount, total);
        int endIdx  = total - _viewOffset;          // 오른쪽 끝 인덱스 (exclusive)
        int startIdx = Math.Max(0, endIdx - count);
        endIdx = startIdx + count;

        // 뷰포트 캔들 슬라이스
        var candles = allCandles.Skip(startIdx).Take(endIdx - startIdx).ToList();
        if (candles.Count < 2) return;

        // ── 레이아웃 ──────────────────────────────────────
        const double padL = 62, padR = 16, padT = 8, padB = 8;
        double cw = w - padL - padR;
        double ch = h - padT - padB;

        double minP = (double)candles.Min(c => c.Low);
        double maxP = (double)candles.Max(c => c.High);
        double rng  = maxP - minP;
        if (rng == 0) rng = 1;
        double pad5 = rng * 0.06;
        minP -= pad5; maxP += pad5; rng = maxP - minP;

        // 오른쪽 여유 슬롯: 최신 데이터 보기일 때만
        int extraSlots = (_viewOffset == 0) ? 2 : 0;
        int slots = candles.Count + extraSlots;
        double Px(int i)    => padL + i / (double)(slots - 1) * cw;
        double Py(double p) => padT + (maxP - p) / rng * ch;

        // ── 그리드 + 레이블 ───────────────────────────────
        var gridPen = new Pen(new SolidColorBrush(gridColor), 0.5);
        var axisPen = new Pen(new SolidColorBrush(axisColor), 1.0);
        var lb      = new SolidColorBrush(labelColor);
        var tf      = new Typeface("Sans-Serif");

        for (int g = 0; g <= 4; g++)
        {
            double y     = padT + g * ch / 4.0;
            double price = maxP - g / 4.0 * rng;
            ctx.DrawLine(gridPen, new Point(padL, y), new Point(padL + cw, y));
            string lbl = price >= 10000 ? $"{price:N0}" : price >= 100 ? $"{price:F1}" : $"{price:F4}";
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 9, lb);
            ctx.DrawText(ft, new Point(padL - ft.Width - 5, y - ft.Height / 2));
        }

        ctx.DrawLine(axisPen, new Point(padL, padT), new Point(padL, padT + ch));
        ctx.DrawLine(axisPen, new Point(padL, padT + ch), new Point(padL + cw, padT + ch));

        // ── 차트 렌더링 ───────────────────────────────────
        using (ctx.PushClip(new Rect(padL, padT, cw, ch + 1)))
        {
            bool isLiveView = (_viewOffset == 0); // 최신 데이터 보기 중
            if (ChartType == ChartType.Candle)
                RenderCandle(ctx, candles, slots, Px, Py, bullColor, bearColor, isLiveView);
            else
                RenderLine(ctx, candles, cw, ch, padL, padT, Px, Py, lineColor, fillTop, fillBot);
        }

        // ── 현재가 점선 + 레이블 ──────────────────────────
        double lastPrice = (double)allCandles[^1].Close;
        double curY      = Py(lastPrice);
        // curY가 차트 범위 안에 있을 때만 표시
        if (curY >= padT && curY <= padT + ch)
        {
            var dashPen = new Pen(new SolidColorBrush(curPriceColor), 1, new DashStyle(new[] { 5.0, 4.0 }, 0));
            ctx.DrawLine(dashPen, new Point(padL, curY), new Point(padL + cw, curY));
            string priceLbl = lastPrice >= 10000 ? $"{lastPrice:N2}" : lastPrice >= 100 ? $"{lastPrice:F2}" : $"{lastPrice:F5}";
            var pft = new FormattedText(priceLbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Sans-Serif", FontStyle.Normal, FontWeight.Bold), 9.5,
                new SolidColorBrush(curPriceColor));
            ctx.DrawText(pft, new Point(padL - pft.Width - 5, curY - pft.Height / 2));
        }
    }

    // ── 라인 차트 ──────────────────────────────────────
    private static void RenderLine(DrawingContext ctx, IReadOnlyList<Candle> candles,
        double cw, double ch, double padL, double padT,
        Func<int, double> Px, Func<double, double> Py,
        Color lineColor, Color fillTop, Color fillBot)
    {
        var sg = new StreamGeometry();
        using (var sgc = sg.Open())
        {
            sgc.BeginFigure(new Point(Px(0), padT + ch), isFilled: true);
            sgc.LineTo(new Point(Px(0), Py((double)candles[0].Close)));
            for (int i = 1; i < candles.Count; i++)
                sgc.LineTo(new Point(Px(i), Py((double)candles[i].Close)));
            sgc.LineTo(new Point(Px(candles.Count - 1), padT + ch));
            sgc.EndFigure(isClosed: true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint    = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint      = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop { Color = fillTop, Offset = 0 },
                new GradientStop { Color = fillBot, Offset = 1 }
            }
        };
        ctx.DrawGeometry(fill, null, sg);
        var linePen = new Pen(new SolidColorBrush(lineColor), 1.8);
        for (int i = 1; i < candles.Count; i++)
            ctx.DrawLine(linePen,
                new Point(Px(i - 1), Py((double)candles[i - 1].Close)),
                new Point(Px(i),     Py((double)candles[i].Close)));
    }

    // ── 캔들스틱 차트 ──────────────────────────────────
    private static void RenderCandle(DrawingContext ctx, IReadOnlyList<Candle> candles,
        int slots, Func<int, double> Px, Func<double, double> Py,
        Color bullColor, Color bearColor, bool isLiveView)
    {
        int    n        = candles.Count;
        // slots 기준으로 간격 계산 (여유 공간 포함)
        double slotW    = Px(1) - Px(0);
        double candleW  = Math.Max(1.5, slotW * 0.7);
        double halfW    = candleW / 2;

        for (int i = 0; i < n; i++)
        {
            var c      = candles[i];
            double x   = Px(i);
            bool bull  = c.Close >= c.Open;
            bool isLast = isLiveView && i == n - 1;

            // 라이브 캔들은 테두리 강조
            var color  = bull ? bullColor : bearColor;
            var brush  = new SolidColorBrush(color);
            var pen    = new Pen(brush, 1);
            var rimPen = isLast ? new Pen(new SolidColorBrush(Color.Parse("#FFC107")), 1) : pen;

            double oY = Py((double)c.Open);
            double cY = Py((double)c.Close);
            double hY = Py((double)c.High);
            double lY = Py((double)c.Low);

            double bodyTop = Math.Min(oY, cY);
            double bodyH   = Math.Max(1, Math.Abs(oY - cY));

            ctx.DrawRectangle(brush, isLast ? rimPen : null, new Rect(x - halfW, bodyTop, candleW, bodyH));
            ctx.DrawLine(pen, new Point(x, hY),               new Point(x, bodyTop));
            ctx.DrawLine(pen, new Point(x, bodyTop + bodyH),  new Point(x, lY));
        }
    }
}
