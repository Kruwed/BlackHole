using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.Models.FileSystem;

public class FileSystemModel
{
	public void OpenFolderInFileExplorer(string dirPath)
	{
		IoHelpers.OpenFolderInFileExplorer(dirPath);
	}
}
