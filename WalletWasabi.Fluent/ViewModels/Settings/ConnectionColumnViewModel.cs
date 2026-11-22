using WalletWasabi.Fluent.Models.UI;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// One of the four columns of the settings that connect the application to something: what it is about, what mark
/// its "Connect to" box has, and whether it can be used. Its two buttons are in the screen itself.
/// </summary>
public partial class ConnectionColumnViewModel : ReactiveObject
{
	/// <summary>Whether the connection is configured and works at the moment. Its box has a check mark then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWorking;

	/// <summary>Whether the connection is configured and does not work at the moment. Its box has an X then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isNotWorking;

	/// <summary>Whether the page of the connection can be opened.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isOpen;

	/// <summary>Whether it is the turn of this connection on the welcome. Its buttons stand out.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isCurrent;

	public ConnectionColumnViewModel(Connection connection)
	{
		Connection = connection;
	}

	public Connection Connection { get; }

	public string Title => Connection.ToString();

	public void Update(ConnectionState state, bool isOpen, bool isCurrent)
	{
		IsWorking = state == ConnectionState.Working;
		IsNotWorking = state == ConnectionState.NotWorking;
		IsOpen = isOpen;
		IsCurrent = isCurrent;
	}
}
