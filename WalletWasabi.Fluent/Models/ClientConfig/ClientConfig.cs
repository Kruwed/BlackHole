namespace WalletWasabi.Fluent.Models.ClientConfig;

public class ClientConfigModel
{
	private readonly IServices _services;

	public ClientConfigModel(IServices services)
	{
		_services = services;
	}

	public string DataDir => _services.DataDir;
}
