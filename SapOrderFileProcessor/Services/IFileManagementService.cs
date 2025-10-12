using System.Collections.Generic;
using System.Threading.Tasks;

namespace SapOrderFileProcessor.Services;

public interface IFileManagementService
{
	Task<string> CreateOrderFolderAsync(int docNum, string cardCode);
	Task<List<string>> FindComponentFilesAsync(string itemCode);
	Task<List<string>> CopyFilesAsync(List<string> sourceFiles, string destinationFolder);
	Task<bool> ValidateSearchFoldersAsync();
}
