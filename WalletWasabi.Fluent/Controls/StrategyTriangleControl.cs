using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using WalletWasabi.JoinMarket.OrderBook;

namespace WalletWasabi.Fluent.Controls;

/// <summary>
/// The triangle that a strategy is picked in: an upright triangle with a dot and a name on each corner, faint dashed
/// lines from every corner to the middle of the side across, and a marker where the strategy sits. A click or a drag
/// moves the marker, and so do the arrow keys.
/// </summary>
/// <remarks>
/// The triangle is drawn in a field of 340 by 206, which is fitted into the room that the control has. Where the
/// marker is, is told as three weights, see <see cref="StrategyTriangle"/>. Without weights there is no marker: the
/// strategy was changed by hand since the triangle set it.
/// </remarks>
public class StrategyTriangleControl : Control
{
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

	public static readonly StyledProperty<TriangleWeights?> WeightsProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, TriangleWeights?>(nameof(Weights), defaultBindingMode: BindingMode.TwoWay);

	public static readonly StyledProperty<string> TopLabelProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, string>(nameof(TopLabel), "");

	public static readonly StyledProperty<string> LeftLabelProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, string>(nameof(LeftLabel), "");

	public static readonly StyledProperty<string> RightLabelProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, string>(nameof(RightLabel), "");

	/// <summary>The colour of the dots and of the marker.</summary>
	public static readonly StyledProperty<IBrush?> AccentProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, IBrush?>(nameof(Accent), Brushes.Orange);

	/// <summary>The colour of the names of the corners that count, and of the ring of the marker.</summary>
	public static readonly StyledProperty<IBrush?> ForegroundProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, IBrush?>(nameof(Foreground), Brushes.White);

	/// <summary>The colour of the names of the corners that count nothing.</summary>
	public static readonly StyledProperty<IBrush?> DimForegroundProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, IBrush?>(nameof(DimForeground), Brushes.Gray);

	/// <summary>The colour of the sides of the triangle and of the dashed lines.</summary>
	public static readonly StyledProperty<IBrush?> StrokeProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, IBrush?>(nameof(Stroke), Brushes.DimGray);

	/// <summary>The colour that the triangle is filled with.</summary>
	public static readonly StyledProperty<IBrush?> FillProperty =
		AvaloniaProperty.Register<StrategyTriangleControl, IBrush?>(nameof(Fill));

	/// <summary>How far an arrow key moves the marker, in the units of the field.</summary>
	private const double KeyStep = 6;

	private const double LabelFontSize = 11;

	static StrategyTriangleControl()
	{
		AffectsRender<StrategyTriangleControl>(
			WeightsProperty,
			TopLabelProperty,
			LeftLabelProperty,
			RightLabelProperty,
			AccentProperty,
			ForegroundProperty,
			DimForegroundProperty,
			StrokeProperty,
			FillProperty);

		FocusableProperty.OverrideDefaultValue<StrategyTriangleControl>(true);
		CursorProperty.OverrideDefaultValue<StrategyTriangleControl>(HandCursor);
	}

	/// <summary>Where the marker is. Null for no marker.</summary>
	public TriangleWeights? Weights
	{
		get => GetValue(WeightsProperty);
		set => SetValue(WeightsProperty, value);
	}

	public string TopLabel
	{
		get => GetValue(TopLabelProperty);
		set => SetValue(TopLabelProperty, value);
	}

	public string LeftLabel
	{
		get => GetValue(LeftLabelProperty);
		set => SetValue(LeftLabelProperty, value);
	}

	public string RightLabel
	{
		get => GetValue(RightLabelProperty);
		set => SetValue(RightLabelProperty, value);
	}

	public IBrush? Accent
	{
		get => GetValue(AccentProperty);
		set => SetValue(AccentProperty, value);
	}

	public IBrush? Foreground
	{
		get => GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	public IBrush? DimForeground
	{
		get => GetValue(DimForegroundProperty);
		set => SetValue(DimForegroundProperty, value);
	}

	public IBrush? Stroke
	{
		get => GetValue(StrokeProperty);
		set => SetValue(StrokeProperty, value);
	}

	public IBrush? Fill
	{
		get => GetValue(FillProperty);
		set => SetValue(FillProperty, value);
	}

	/// <summary>The weights that a point of the control stands for.</summary>
	public static TriangleWeights GetWeights(Point point, Size size) =>
		StrategyTriangle.GetWeights(point.X, point.Y, size.Width, size.Height);

	public override void Render(DrawingContext context)
	{
		base.Render(context);

		// The whole control takes clicks, not only where something is drawn.
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

		var (scale, offset) = GetTransform(Bounds.Size);

		Point At((double X, double Y) position) =>
			new(offset.X + (position.X - StrategyTriangle.FieldLeft) * scale, offset.Y + position.Y * scale);

		var top = At(StrategyTriangle.Top);
		var left = At(StrategyTriangle.Left);
		var right = At(StrategyTriangle.Right);

		var triangle = new StreamGeometry();

		using (var geometry = triangle.Open())
		{
			geometry.BeginFigure(top, isFilled: true);
			geometry.LineTo(left);
			geometry.LineTo(right);
			geometry.EndFigure(isClosed: true);
		}

		context.DrawGeometry(Fill, new Pen(Stroke, 1.5 * scale), triangle);

		// From every corner to the middle of the side across.
		var dashed = new Pen(Stroke, 1 * scale, new DashStyle([2, 4], 0)) { LineCap = PenLineCap.Flat };

		using (context.PushOpacity(0.6))
		{
			context.DrawLine(dashed, top, Middle(left, right));
			context.DrawLine(dashed, left, Middle(top, right));
			context.DrawLine(dashed, right, Middle(top, left));
		}

		foreach (var corner in (Point[])[top, left, right])
		{
			context.DrawEllipse(Accent, null, corner, 3.5 * scale, 3.5 * scale);
		}

		if (Weights is { } weights)
		{
			var marker = At(StrategyTriangle.GetPosition(weights));

			using (context.PushOpacity(0.22))
			{
				context.DrawEllipse(Accent, null, marker, 10 * scale, 10 * scale);
			}

			context.DrawEllipse(Accent, new Pen(Foreground, 1.5 * scale), marker, 5 * scale, 5 * scale);
		}

		// A name is bright while its corner counts.
		DrawLabel(context, TopLabel, At((130, 28)), scale, Weights is { Top: > 0 });
		DrawLabel(context, LeftLabel, At((56, 200)), scale, Weights is { Left: > 0 });
		DrawLabel(context, RightLabel, At((204, 200)), scale, Weights is { Right: > 0 });
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var width = double.IsInfinity(availableSize.Width) ? StrategyTriangle.FieldWidth : availableSize.Width;
		var height = width * StrategyTriangle.FieldHeight / StrategyTriangle.FieldWidth;

		if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
		{
			height = availableSize.Height;
			width = height * StrategyTriangle.FieldWidth / StrategyTriangle.FieldHeight;
		}

		return new Size(width, height);
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);

		if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			Focus();
			e.Pointer.Capture(this);
			Weights = GetWeights(e.GetPosition(this), Bounds.Size);
			e.Handled = true;
		}
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);

		if (ReferenceEquals(e.Pointer.Captured, this) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			Weights = GetWeights(e.GetPosition(this), Bounds.Size);
			e.Handled = true;
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);

		if (ReferenceEquals(e.Pointer.Captured, this))
		{
			e.Pointer.Capture(null);
		}
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);

		var (dx, dy) = e.Key switch
		{
			Key.Left => (-KeyStep, 0.0),
			Key.Right => (KeyStep, 0.0),
			Key.Up => (0.0, -KeyStep),
			Key.Down => (0.0, KeyStep),
			_ => (0.0, 0.0)
		};

		if (dx == 0 && dy == 0)
		{
			return;
		}

		// Without a marker the keys start from the middle.
		var (x, y) = StrategyTriangle.GetPosition(Weights ?? TriangleWeights.Centre);

		Weights = StrategyTriangle.GetWeights(x + dx, y + dy);
		e.Handled = true;
	}

	/// <summary>How the field is fitted into a room: by how much it is scaled, and where its top left corner is.</summary>
	private static (double Scale, Point Offset) GetTransform(Size size)
	{
		var (scale, offsetX, offsetY) = StrategyTriangle.Fit(size.Width, size.Height);

		return (scale, new Point(offsetX, offsetY));
	}

	private static Point Middle(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

	private void DrawLabel(DrawingContext context, string text, Point centre, double scale, bool isBright)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		var formatted = new FormattedText(
			text,
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			Typeface.Default,
			LabelFontSize * Math.Max(1, scale),
			isBright ? Foreground : DimForeground);

		// The position names the middle of the baseline, as it does in the drawing that the triangle was designed in.
		context.DrawText(formatted, new Point(centre.X - formatted.Width / 2, centre.Y - formatted.Baseline));
	}
}
