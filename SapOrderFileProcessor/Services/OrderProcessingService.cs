using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SapOrderFileProcessor.Models;

namespace SapOrderFileProcessor.Services;

public sealed class OrderProcessingService : IOrderProcessingService
{
	private readonly ISapServiceLayerService _sapService;
	private readonly IFileManagementService _fileService;
	private readonly ILogger<OrderProcessingService> _logger;

	public OrderProcessingService(ISapServiceLayerService sapService, IFileManagementService fileService, ILogger<OrderProcessingService> logger)
	{
		_sapService = sapService;
		_fileService = fileService;
		_logger = logger;
	}

	public async Task<ProcessingResult> ProcessOrderAsync(int docEntry, CancellationToken cancellationToken = default)
	{
		var result = new ProcessingResult();
        SapSession? session = null;
		var stopwatch = Stopwatch.StartNew();

		try
		{
			if (docEntry <= 0) throw new ArgumentOutOfRangeException(nameof(docEntry));

			_logger.LogInformation("Inizio processamento ordine DocEntry: {DocEntry}", docEntry);

			// Validazione cartelle di ricerca accessibili
			var searchFoldersOk = await _fileService.ValidateSearchFoldersAsync();
			if (!searchFoldersOk)
			{
				throw new InvalidOperationException("Cartelle di ricerca non accessibili");
			}

			// Login
            session = await _sapService.LoginAsync();

			// Ordine
            var order = await _sapService.GetSalesOrderAsync(docEntry, session);
            result.OrderDocNum = order.DocNum;
            result.Project = order.Project;
			_logger.LogInformation("Ordine recuperato: DocNum={DocNum}, Cliente={Cliente}", order.DocNum, order.CardName);

			// I nomi file aggiuntivi ora sono sui campi utente della singola riga ordine

            int processedComponents = 0;
            int copiedFiles = 0;
            int processedOrderItems = 0;

			foreach (var item in order.Items)
			{
				cancellationToken.ThrowIfCancellationRequested();

				// Create folder for this specific item (based on project and warehouse)
                var orderFolder = await _fileService.CreateOrderFolderAsync(order.DocNum, order.CardCode, order.Project, item.WarehouseCode);
                if (!string.IsNullOrWhiteSpace(orderFolder))
                {
                    result.CreatedDestinationFolders.Add(orderFolder);
                }

				// Copia anche gli eventuali file aggiuntivi specificati nei campi utente della riga (qualsiasi estensione)
				var rowFileNames = new System.Collections.Generic.List<string>();
				if (!string.IsNullOrWhiteSpace(item.ParNdSip)) rowFileNames.Add(item.ParNdSip);
				if (!string.IsNullOrWhiteSpace(item.ParNdiv1)) rowFileNames.Add(item.ParNdiv1);
				if (rowFileNames.Any())
				{
					try
					{
						var extraFiles = await _fileService.FindFilesByExactNamesAsync(rowFileNames);
						if (extraFiles.Count > 0)
						{
							var extraCopied = await _fileService.CopyFilesAsync(extraFiles, orderFolder);
							copiedFiles += extraCopied.Count;
						}
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Errore copia file aggiuntivi per DocEntry={DocEntry}", docEntry);
						result.Errors.Add($"Errore copia file aggiuntivi: {ex.Message}");
					}
				}

				BillOfMaterial bom;
				try
				{
                    bom = await _sapService.GetBillOfMaterialAsync(item.ItemCode, session);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Errore recupero distinta base per {ItemCode}", item.ItemCode);
					result.Errors.Add($"Errore distinta base {item.ItemCode}: {ex.Message}");
					bom = new BillOfMaterial { ParentItemCode = item.ItemCode };
				}

                if (bom.Components.Any())
				{
					_logger.LogInformation("Articolo {ItemCode}: trovati {Count} componenti in distinta base", item.ItemCode, bom.Components.Count);
					foreach (var component in bom.Components)
					{
						copiedFiles += await ProcessComponentAsync(component.ItemCode, orderFolder, result);
						processedComponents++;
					}
				}
				else
				{
					_logger.LogWarning("Articolo {ItemCode}: nessuna distinta base. Verrà processato l'articolo stesso.", item.ItemCode);
					copiedFiles += await ProcessComponentAsync(item.ItemCode, orderFolder, result);
					processedComponents++;
				}

                processedOrderItems++;
			}

			result.ProcessedComponents = processedComponents;
			result.CopiedFiles = copiedFiles;
            result.ProcessedOrderItems = processedOrderItems;
			result.Success = result.Errors.Count == 0;
		}
		catch (Exception ex)
		{
			result.Success = false;
			result.Errors.Add(ex.Message);
			_logger.LogError(ex, "Errore durante il processamento dell'ordine DocEntry={DocEntry}", docEntry);
		}
		finally
		{
			stopwatch.Stop();
			result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
            if (session?.SessionId != null)
			{
				try
				{
                    await _sapService.LogoutAsync(session.SessionId);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Errore durante logout");
				}
			}
			_logger.LogInformation("Processamento completato: {Components} componenti, {Files} file copiati", result.ProcessedComponents, result.CopiedFiles);
		}

		return result;
	}

	private async Task<int> ProcessComponentAsync(string itemCode, string orderFolder, ProcessingResult result)
	{
		try
		{
			var files = await _fileService.FindComponentFilesAsync(itemCode);
			if (files.Count == 0)
			{
				var msg = $"Componente {itemCode}: nessun file trovato nella master folder";
				_logger.LogWarning(msg);
				result.WarningMessages.Add(msg);
                result.MissingComponentItemCodes.Add(itemCode);
				return 0;
			}

				var copied = await _fileService.CopyFilesAsync(files, orderFolder);
			return copied.Count;
		}
		catch (Exception ex)
		{
			var msg = $"Errore processamento componente {itemCode}: {ex.Message}";
			_logger.LogError(ex, msg);
			result.Errors.Add(msg);
			return 0;
		}
	}
}
