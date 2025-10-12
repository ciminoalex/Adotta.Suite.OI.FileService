using System.Collections.Generic;

namespace SapOrderFileProcessor.Models;

public sealed class BillOfMaterial
{
	public string ParentItemCode { get; set; } = string.Empty;
	public List<BomComponent> Components { get; set; } = new();
}
