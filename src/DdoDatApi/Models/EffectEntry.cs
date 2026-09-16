using VoK.Sdk.Properties;

namespace DdoDatApi.Models;

internal record EffectEntry
{
    public uint EffectId { get; init; }
    public IPropertyCollection Props { get; init; }
    public string FallbackName { get; init; }
}
