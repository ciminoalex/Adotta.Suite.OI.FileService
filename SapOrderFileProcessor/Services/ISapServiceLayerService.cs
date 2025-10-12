using System.Threading.Tasks;
using SapOrderFileProcessor.Models;

namespace SapOrderFileProcessor.Services;

public interface ISapServiceLayerService
{
    Task<SapSession> LoginAsync();
    Task<SalesOrder> GetSalesOrderAsync(int docEntry, SapSession session);
    Task<BillOfMaterial> GetBillOfMaterialAsync(string itemCode, SapSession session);
    Task LogoutAsync(string sessionId);
}
