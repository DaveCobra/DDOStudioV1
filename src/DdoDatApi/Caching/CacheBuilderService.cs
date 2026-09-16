using System.Threading;
using System.Threading.Tasks;

namespace DdoDatApi.Caching;

public class CacheBuilderService : ExportService, ICacheBuilderService
{
    public override Task Export(CancellationToken token)
    {
        IndexLoader.RefreshCacheFromDats(token);
        return Task.CompletedTask;
    }
}
