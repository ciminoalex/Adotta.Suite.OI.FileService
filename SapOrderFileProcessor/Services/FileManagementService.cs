using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapOrderFileProcessor.Models;

namespace SapOrderFileProcessor.Services;

public sealed class FileManagementService : IFileManagementService
{
	private readonly ILogger<FileManagementService> _logger;
	private readonly SapConfiguration _config;

	public FileManagementService(ILogger<FileManagementService> logger, IOptions<SapConfiguration> options)
	{
		_logger = logger;
		_config = options.Value;
	}

	public Task<string> CreateOrderFolderAsync(int docNum, string cardCode, string? project = null, string? warehouseCode = null)
	{
		if (docNum <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(docNum));
		}

		if (string.IsNullOrWhiteSpace(cardCode))
		{
			throw new ArgumentException("CardCode non può essere vuoto", nameof(cardCode));
		}

		string baseFolder = _config.DestinationBaseFolder ?? string.Empty;
		if (string.IsNullOrWhiteSpace(baseFolder))
		{
			throw new DirectoryNotFoundException("DestinationBaseFolder non configurata");
		}

		// Determine client folder based on CardCode
		string clientFolder = GetClientFolder(cardCode);
		
		// Build folder path with project and warehouse prefix
		var pathParts = new List<string> { baseFolder, clientFolder };
		
		// Add project subdirectory if available
		if (!string.IsNullOrWhiteSpace(project))
		{
			pathParts.Add(SanitizeFileName(project));
		}
		
		// Add warehouse prefix to folder name
		string folderName = $"Order_{SanitizeFileName(docNum.ToString())}";
		if (!string.IsNullOrWhiteSpace(warehouseCode))
		{
			string prefix = GetWarehousePrefix(warehouseCode);
			if (!string.IsNullOrWhiteSpace(prefix))
			{
				folderName = $"{prefix} {SanitizeFileName(docNum.ToString())}";
			}
		}
		
		pathParts.Add(folderName);
		string destination = Path.Combine(pathParts.ToArray());

		try
		{
			Directory.CreateDirectory(destination);
			_logger.LogInformation("Cartella creata: {Path} per cliente {CardCode}, progetto {Project}, magazzino {Warehouse}", 
				destination, cardCode, project ?? "N/A", warehouseCode ?? "N/A");
			return Task.FromResult(destination);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Errore creazione cartella {Path}", destination);
			throw;
		}
	}

	public Task<List<string>> FindComponentFilesAsync(string itemCode)
	{
		if (_config.SearchFolders == null || _config.SearchFolders.Count == 0)
		{
			throw new DirectoryNotFoundException("SearchFolders non configurate");
		}

		// Estrai gli ultimi 7 caratteri dopo aver rimosso eventuali suffissi numerici (.1, .2, .3, .4)
		string searchPattern = ExtractLastSevenCharacters(itemCode);
		
		string patternTemplate = string.IsNullOrWhiteSpace(_config.FileSearchPattern) ? "{ItemCode}*.*" : _config.FileSearchPattern;
		string pattern = patternTemplate.Replace("{ItemCode}", SanitizePattern(searchPattern));

		// Implementazione con gestione duplicati basata sulla cartella padre
		var regex = WildcardToRegex(pattern, ignoreCase: true);
		var allMatches = new List<string>();
		
		foreach (var searchFolder in _config.SearchFolders)
		{
			if (string.IsNullOrWhiteSpace(searchFolder))
			{
				_logger.LogWarning("Cartella di ricerca vuota ignorata");
				continue;
			}

			if (!Directory.Exists(searchFolder))
			{
				_logger.LogWarning("Cartella di ricerca non trovata: {Path}", searchFolder);
				continue;
			}

			try
			{
				foreach (var file in Directory.EnumerateFiles(searchFolder, "*", SearchOption.AllDirectories))
				{
					var name = Path.GetFileName(file);
					if (regex.IsMatch(name))
					{
						allMatches.Add(file);
					}
				}
				_logger.LogInformation("{ItemCode}: cercato pattern '{Pattern}' in {Path}", itemCode, searchPattern, searchFolder);
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permessi insufficienti su cartella di ricerca {Path}", searchFolder);
			}
			catch (IOException ex)
			{
				_logger.LogError(ex, "Errore I/O in lettura cartella di ricerca {Path}", searchFolder);
			}
		}

		// Gestisci duplicati basandosi sulla cartella padre
		var finalMatches = ResolveDuplicateFiles(allMatches, itemCode);
		
		_logger.LogInformation("{ItemCode}: trovati {Count} file totali, selezionati {SelectedCount} dopo risoluzione duplicati", 
			itemCode, allMatches.Count, finalMatches.Count);
		return Task.FromResult(finalMatches);
	}

	public async Task<List<string>> CopyFilesAsync(List<string> sourceFiles, string destinationFolder)
	{
		var copied = new List<string>();
		if (sourceFiles == null || sourceFiles.Count == 0) return copied;

		foreach (var src in sourceFiles)
		{
			try
			{
				if (!File.Exists(src))
				{
					_logger.LogWarning("File sorgente non trovato: {File}", src);
					continue;
				}

				string fileName = Path.GetFileName(src);
				string destPath = Path.Combine(destinationFolder, SanitizeFileName(fileName));

				string finalPath = ResolveConflictPath(destPath);
				File.Copy(src, finalPath);
				_logger.LogInformation("File copiato: {Src} -> {Dest}", src, finalPath);
				copied.Add(finalPath);
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permessi insufficienti copia file: {File}", src);
			}
			catch (DirectoryNotFoundException ex)
			{
				_logger.LogError(ex, "Destinazione non valida: {Dest}", destinationFolder);
			}
			catch (IOException ex)
			{
				_logger.LogError(ex, "Errore I/O copia file: {File}", src);
			}
			await Task.Yield();
		}

		return copied;
	}

	public Task<bool> ValidateSearchFoldersAsync()
	{
		try
		{
			if (_config.SearchFolders == null || _config.SearchFolders.Count == 0)
			{
				_logger.LogError("SearchFolders non configurate");
				return Task.FromResult(false);
			}

			foreach (var folder in _config.SearchFolders)
			{
				if (string.IsNullOrWhiteSpace(folder))
				{
					_logger.LogError("Cartella di ricerca vuota trovata");
					return Task.FromResult(false);
				}

				if (!Directory.Exists(folder))
				{
					_logger.LogError("Cartella di ricerca non trovata: {Path}", folder);
					return Task.FromResult(false);
				}
			}

			return Task.FromResult(true);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Errore validazione SearchFolders");
			return Task.FromResult(false);
		}
	}

	private static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
	}

	private static string SanitizePattern(string value)
	{
		// Non sostituire wildcard ma rimuovere caratteri non validi per nomi file
		var invalid = Path.GetInvalidFileNameChars().Except(new[] { '*', '?' }).ToHashSet();
		return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
	}

	private static Regex WildcardToRegex(string pattern, bool ignoreCase)
	{
		string escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
		return new Regex($"^{escaped}$", ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
	}

	private static string ResolveExtension(string path)
	{
		return Path.GetExtension(path);
	}

	private static string ResolveConflictPath(string desiredPath)
	{
		if (!File.Exists(desiredPath)) return desiredPath;
		string directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
		string fileNameWithoutExt = Path.GetFileNameWithoutExtension(desiredPath);
		string ext = ResolveExtension(desiredPath);
		int suffix = 1;
		string candidate;
		do
		{
			candidate = Path.Combine(directory, $"{fileNameWithoutExt}_{suffix}{ext}");
			suffix++;
		} while (File.Exists(candidate));
		return candidate;
	}

	private string GetClientFolder(string cardCode)
	{
		if (_config.ClientFolderMappings != null && _config.ClientFolderMappings.TryGetValue(cardCode, out var mappedFolder))
		{
			return mappedFolder;
		}

		return _config.DefaultClientFolder ?? "Adotta Italia Srl";
	}

	private string GetWarehousePrefix(string warehouseCode)
	{
		if (_config.WarehousePrefixMappings != null && _config.WarehousePrefixMappings.TryGetValue(warehouseCode, out var prefix))
		{
			return prefix;
		}

		// Default prefix if no mapping found
		return "NA";
	}

	/// <summary>
	/// Estrae gli ultimi 7 caratteri dell'ItemCode dopo aver rimosso eventuali suffissi numerici (.1, .2, .3, .4)
	/// </summary>
	/// <param name="itemCode">Il codice articolo completo</param>
	/// <returns>Gli ultimi 7 caratteri senza suffissi numerici</returns>
	private static string ExtractLastSevenCharacters(string itemCode)
	{
		if (string.IsNullOrWhiteSpace(itemCode))
		{
			return string.Empty;
		}

		// Rimuovi eventuali suffissi numerici (.1, .2, .3, .4)
		string cleanedItemCode = itemCode;
		var suffixPattern = new Regex(@"\.\d+$");
		cleanedItemCode = suffixPattern.Replace(cleanedItemCode, string.Empty);

		// Estrai gli ultimi 7 caratteri
		if (cleanedItemCode.Length >= 7)
		{
			return cleanedItemCode.Substring(cleanedItemCode.Length - 7);
		}

		// Se l'ItemCode è più corto di 7 caratteri, restituisci tutto
		return cleanedItemCode;
	}

	/// <summary>
	/// Risolve i file duplicati selezionando quelli che si trovano in cartelle il cui nome corrisponde alla parte iniziale dell'ItemCode
	/// </summary>
	/// <param name="allFiles">Lista di tutti i file trovati</param>
	/// <param name="itemCode">Il codice articolo completo</param>
	/// <returns>Lista dei file selezionati dopo la risoluzione dei duplicati</returns>
	private List<string> ResolveDuplicateFiles(List<string> allFiles, string itemCode)
	{
		if (allFiles == null || allFiles.Count == 0)
		{
			return new List<string>();
		}

		// Estrai la parte iniziale dell'ItemCode (prima del primo punto dopo il prefisso)
		string itemCodePrefix = ExtractItemCodePrefix(itemCode);
		
		// Raggruppa i file per nome
		var groupedFiles = allFiles.GroupBy(f => Path.GetFileName(f).ToLowerInvariant())
			.ToDictionary(g => g.Key, g => g.ToList());

		var selectedFiles = new List<string>();

		foreach (var group in groupedFiles)
		{
			var filesWithSameName = group.Value;
			
			if (filesWithSameName.Count == 1)
			{
				// Nessun duplicato, aggiungi il file
				selectedFiles.Add(filesWithSameName[0]);
			}
			else
			{
				// Ci sono duplicati, cerca quello nella cartella corretta
				var selectedFile = SelectFileByParentFolder(filesWithSameName, itemCodePrefix);
				selectedFiles.Add(selectedFile);
				
				_logger.LogInformation("Duplicati trovati per '{FileName}': selezionato {SelectedPath}", 
					group.Key, selectedFile);
			}
		}

		return selectedFiles;
	}

	/// <summary>
	/// Estrae il prefisso dell'ItemCode (es. "PAP.0171" da "PAP.0171.A01-2590-A-EB317")
	/// </summary>
	/// <param name="itemCode">Il codice articolo completo</param>
	/// <returns>Il prefisso dell'ItemCode</returns>
	private static string ExtractItemCodePrefix(string itemCode)
	{
		if (string.IsNullOrWhiteSpace(itemCode))
		{
			return string.Empty;
		}

		// Rimuovi eventuali suffissi numerici (.1, .2, .3, .4)
		string cleanedItemCode = itemCode;
		var suffixPattern = new Regex(@"\.\d+$");
		cleanedItemCode = suffixPattern.Replace(cleanedItemCode, string.Empty);

		// Trova il primo punto dopo il prefisso (es. PAP.0171.A01-2590-A-EB317 -> PAP.0171)
		var parts = cleanedItemCode.Split('.');
		if (parts.Length >= 2)
		{
			return $"{parts[0]}.{parts[1]}";
		}

		// Se non ci sono abbastanza parti, restituisci la prima parte
		return parts.Length > 0 ? parts[0] : string.Empty;
	}

	/// <summary>
	/// Seleziona il file corretto tra i duplicati basandosi sulla cartella padre
	/// </summary>
	/// <param name="duplicateFiles">Lista dei file duplicati</param>
	/// <param name="itemCodePrefix">Prefisso dell'ItemCode da cercare nella cartella padre</param>
	/// <returns>Il file selezionato</returns>
	private string SelectFileByParentFolder(List<string> duplicateFiles, string itemCodePrefix)
	{
		// Prima prova: cerca un file in una cartella che corrisponde esattamente al prefisso
		foreach (var file in duplicateFiles)
		{
			var parentFolder = Path.GetDirectoryName(file);
			var folderName = Path.GetFileName(parentFolder);
			
			if (string.Equals(folderName, itemCodePrefix, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("File selezionato per corrispondenza esatta cartella: {File}", file);
				return file;
			}
		}

		// Seconda prova: cerca un file in una cartella che contiene il prefisso
		foreach (var file in duplicateFiles)
		{
			var parentFolder = Path.GetDirectoryName(file);
			var folderName = Path.GetFileName(parentFolder);
			
			if (!string.IsNullOrEmpty(folderName) && folderName.Contains(itemCodePrefix, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("File selezionato per corrispondenza parziale cartella: {File}", file);
				return file;
			}
		}

		// Terza prova: cerca un file in una cartella che inizia con il prefisso
		foreach (var file in duplicateFiles)
		{
			var parentFolder = Path.GetDirectoryName(file);
			var folderName = Path.GetFileName(parentFolder);
			
			if (!string.IsNullOrEmpty(folderName) && folderName.StartsWith(itemCodePrefix, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("File selezionato per corrispondenza iniziale cartella: {File}", file);
				return file;
			}
		}

		// Fallback: se nessuna corrispondenza, restituisci il primo file
		_logger.LogWarning("Nessuna corrispondenza trovata per prefisso '{Prefix}', selezionato primo file: {File}", 
			itemCodePrefix, duplicateFiles[0]);
		return duplicateFiles[0];
	}
}
