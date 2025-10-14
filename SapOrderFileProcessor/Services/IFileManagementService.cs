using System.Collections.Generic;
using System.Threading.Tasks;

namespace SapOrderFileProcessor.Services;

public interface IFileManagementService
{
	Task<string> CreateOrderFolderAsync(int docNum, string cardCode, string? project = null, string? warehouseCode = null);
	Task<List<string>> FindComponentFilesAsync(string itemCode);
    Task<List<string>> FindFilesByExactNamesAsync(List<string> fileNames);
	Task<List<string>> CopyFilesAsync(List<string> sourceFiles, string destinationFolder);
	Task<bool> ValidateSearchFoldersAsync();
	Task<string?> CreateZipFromFolderAsync(string folderPath, string zipFileNameWithoutExtension);
}
