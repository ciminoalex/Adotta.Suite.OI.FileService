namespace SapOrderFileProcessor.Models;

public sealed class BomComponent
{
	public string ItemCode { get; set; } = string.Empty;
	public string ItemName { get; set; } = string.Empty;
	public decimal Quantity { get; set; }
	public int LineNum { get; set; }
}
