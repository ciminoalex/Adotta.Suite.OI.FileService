using System.Collections.Generic;

namespace SapOrderFileProcessor.Models;

public sealed class ProcessingResult
{
	public bool Success { get; set; }
	public int OrderDocNum { get; set; }
	public int ProcessedComponents { get; set; }
	public int CopiedFiles { get; set; }
	public List<string> Errors { get; set; } = new();
	public List<string> WarningMessages { get; set; } = new();
	public long ExecutionTimeMs { get; set; }
}
