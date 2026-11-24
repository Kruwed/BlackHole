using System.Reactive.Linq;
using System.Windows.Input;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// One of the rows under a strategy triangle: a name, a box for typing an exact value, and a bar that is a slider as
/// well, with a caption on its left and the value on its right.
/// </summary>
/// <remarks>
/// The screen tells the row what to show. What the user does with the row, moving the bar or typing a value, is told
/// to the screen, which then tells every row what to show again.
/// </remarks>
public partial class BarRowViewModel : ReactiveObject
{
	private readonly Action<double> _moved;
	private readonly Action<string> _typed;

	/// <summary>Whether the row is being told what to show: a position that changes then is not the user's doing.</summary>
	private bool _isShowing;

	/// <summary>What the box held when the row was told what to show. A text other than this one was typed.</summary>
	private string _shownText = "";

	[AutoNotify] private double _position;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private double _maximum = 100;
	[AutoNotify] private string _text = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _caption = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _valueText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isEnabled = true;

	/// <param name="label">The name of the row.</param>
	/// <param name="moved">Is told where the user moved the bar to.</param>
	/// <param name="typed">Is told what the user typed into the box, once it is done typing.</param>
	public BarRowViewModel(string label, Action<double> moved, Action<string> typed)
	{
		Label = label;
		_moved = moved;
		_typed = typed;

		this.WhenAnyValue(x => x.Position)
			.Skip(1)
			.Where(_ => !_isShowing)
			.Subscribe(position => _moved(position));

		CommitCommand = ReactiveCommand.Create(Commit);
		CancelCommand = ReactiveCommand.Create(() => { Text = _shownText; });
	}

	public string Label { get; }

	/// <summary>How much of the bar is filled, from nothing to all of it.</summary>
	public double Fill => Maximum > 0 ? Math.Clamp(Position / Maximum, 0, 1) : 0;

	/// <summary>Takes the typed value: Enter does that, and so does leaving the box.</summary>
	public ICommand CommitCommand { get; }

	/// <summary>Gives the typed value up: Escape does that.</summary>
	public ICommand CancelCommand { get; }

	/// <summary>Tells the row what to show.</summary>
	public void Show(double position, double maximum, string caption, string valueText, string text, bool isEnabled = true)
	{
		_isShowing = true;

		try
		{
			Maximum = maximum;
			Position = Math.Clamp(position, 0, maximum);
			Caption = caption;
			ValueText = valueText;
			IsEnabled = isEnabled;
			_shownText = text;
			Text = text;
			this.RaisePropertyChanged(nameof(Fill));
		}
		finally
		{
			_isShowing = false;
		}
	}

	private void Commit()
	{
		if (Text != _shownText)
		{
			_typed(Text.Trim());
		}
	}
}
