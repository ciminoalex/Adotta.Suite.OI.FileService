using System.Collections.Generic;

namespace SapOrderFileProcessor.Models;

public sealed class SalesOrder
{
	public int DocEntry { get; set; }
	public int DocNum { get; set; }
	public string CardCode { get; set; } = string.Empty;
	public string CardName { get; set; } = string.Empty;
	public List<OrderItem> Items { get; set; } = new();
	public string? Project { get; set; }
}
