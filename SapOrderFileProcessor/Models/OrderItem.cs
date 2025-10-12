namespace SapOrderFileProcessor.Models;

public sealed class OrderItem
{
	public string ItemCode { get; set; } = string.Empty;
	public string ItemName { get; set; } = string.Empty;
	public decimal Quantity { get; set; }
	public int LineNum { get; set; }
	public bool HasBom { get; set; }
}
