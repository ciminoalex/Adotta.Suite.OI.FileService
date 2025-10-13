using System.Collections.Generic;

namespace SapOrderFileProcessor.Models;

public sealed class ProcessingResult
{
	public bool Success { get; set; }
	public int OrderDocNum { get; set; }
    public string? Project { get; set; }
    public int ProcessedOrderItems { get; set; }
	public int ProcessedComponents { get; set; }
	public int CopiedFiles { get; set; }
	public List<string> Errors { get; set; } = new();
	public List<string> WarningMessages { get; set; } = new();
    public List<string> CreatedDestinationFolders { get; set; } = new();
    public List<string> MissingComponentItemCodes { get; set; } = new();
	public long ExecutionTimeMs { get; set; }
}
