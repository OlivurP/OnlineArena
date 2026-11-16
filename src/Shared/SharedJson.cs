using System.Text.Json;

namespace Shared;

/// <summary>Fælles JSON-indstillinger (camelCase), så alle services serialiserer ens.</summary>
public static class SharedJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
