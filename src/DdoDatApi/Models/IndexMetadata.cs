using System;

namespace DdoDatApi.Models;

public class IndexMetadata
{
    public string ClientVersion { get; set; }

    public DateTime? CompiledOnUtc { get; set; }

    public long SizeInBytes { get; set; }

    public TimeSpan? CompilationDuration { get; set; }
}
