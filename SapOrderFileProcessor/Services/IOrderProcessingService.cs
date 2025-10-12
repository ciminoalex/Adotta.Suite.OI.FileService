using System.Threading;
using System.Threading.Tasks;
using SapOrderFileProcessor.Models;

namespace SapOrderFileProcessor.Services;

public interface IOrderProcessingService
{
	Task<ProcessingResult> ProcessOrderAsync(int docEntry, CancellationToken cancellationToken = default);
}
