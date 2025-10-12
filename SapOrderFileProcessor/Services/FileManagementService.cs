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

	public Task<string> CreateOrderFolderAsync(int docNum, string cardCode)
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
		string folderName = $"Order_{SanitizeFileName(docNum.ToString())}";
		string destination = Path.Combine(baseFolder, clientFolder, folderName);

		try
		{
			Directory.CreateDirectory(destination);
			_logger.LogInformation("Cartella creata: {Path} per cliente {CardCode}", destination, cardCode);
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

		string patternTemplate = string.IsNullOrWhiteSpace(_config.FileSearchPattern) ? "{ItemCode}*.*" : _config.FileSearchPattern;
		string pattern = patternTemplate.Replace("{ItemCode}", SanitizePattern(itemCode));

		// Implementazione semplice: enumerazione file e filtro con wildcard -> Regex
		var regex = WildcardToRegex(pattern, ignoreCase: true);
		var matches = new List<string>();
		
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
						matches.Add(file);
					}
				}
				_logger.LogInformation("{ItemCode}: cercato in {Path}", itemCode, searchFolder);
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

		_logger.LogInformation("{ItemCode}: trovati {Count} file totali", itemCode, matches.Count);
		return Task.FromResult(matches);
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
}
