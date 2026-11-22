namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// One of the five columns of the welcome screen: what it is about, and how far the user is with it. Its two buttons
/// are in the screen itself.
/// </summary>
public partial class WelcomeColumnViewModel : ReactiveObject
{
	/// <summary>Whether the step is done. The column says so with a check mark.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isDone;

	/// <summary>Whether it is the turn of this step. Its buttons stand out.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isCurrent;

	public WelcomeColumnViewModel(WelcomeStep step)
	{
		Step = step;
	}

	public WelcomeStep Step { get; }

	public string Title => Step.ToString();

	public void Update(WelcomeStep current)
	{
		IsDone = WelcomeProgress.IsDone(Step, current);
		IsCurrent = Step == current;
	}
}
