namespace DdoDatApi.Caching;

public interface ICacheBuilderService
{
    bool IsRunning { get; }
    void Export();
}
