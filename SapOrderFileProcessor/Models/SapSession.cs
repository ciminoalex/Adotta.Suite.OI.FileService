namespace SapOrderFileProcessor.Models;

public sealed class SapSession
{
	public string SessionId { get; set; } = string.Empty;
	public string? RouteId { get; set; }
}


