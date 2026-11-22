namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>What the user sets up on the welcome screen, in the order in which it is set up.</summary>
public enum WelcomeStep
{
	Tor,
	Node,
	Nostr,
	Coordinator,
	Wallet
}

/// <summary>
/// How far the user is with the welcome: Tor, the node, Nostr and the coordinator are set up one after the other, and
/// then the wallet is made. A step is open once every step before it is done.
/// </summary>
/// <remarks>
/// It is kept apart from the welcome screen, because the screen is made anew when a dialog on top of it closed every
/// page, the settings for example. It is not saved: the next start of the application begins with the first step.
/// </remarks>
public partial class WelcomeProgress : ReactiveObject
{
	/// <summary>The first step that is not done yet. The steps before it are done, the ones after it are still closed.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private WelcomeStep _currentStep = WelcomeStep.Tor;

	/// <summary>Takes note that a step is done.</summary>
	public void Complete(WelcomeStep step) => CurrentStep = Advance(CurrentStep, step);

	/// <summary>
	/// Tells whose turn it is once a step is done: the step after it, if it was its turn. A step that was done before can
	/// be done again, which changes nothing, and the wallet is the last step.
	/// </summary>
	public static WelcomeStep Advance(WelcomeStep current, WelcomeStep done) =>
		done == current && current < WelcomeStep.Wallet ? current + 1 : current;

	/// <summary>Whether a step can be taken: it is its turn, or it is done and can be done again.</summary>
	public static bool IsOpen(WelcomeStep step, WelcomeStep current) => step <= current;

	public static bool IsDone(WelcomeStep step, WelcomeStep current) => step < current;
}
