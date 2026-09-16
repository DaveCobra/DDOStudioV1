using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using VoK.Sdk.Properties;

namespace DdoDatApi.Converters;

public class IPropertyJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return typeof(IProperty).IsAssignableFrom(objectType);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        throw new NotImplementedException("Deserialization is not supported.");
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var innerSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = serializer.ContractResolver,
            NullValueHandling = serializer.NullValueHandling,
            Formatting = serializer.Formatting,
        });

        var jo = JObject.FromObject(value, innerSerializer);

        var propertyType = value switch
        {
            IArrayProperty    => "Array",
            IBitFieldProperty => "BitField",
            IByteProperty     => "Byte",
            IDoubleProperty   => "Double",
            IEnumProperty     => "Enum",
            IFloatProperty    => "Float",
            IInt32Property    => "Int32",
            IInt64Property    => "Int64",
            IPositionProperty => "Position",
            IStringInfoProperty => "StringInfo",
            IStringProperty   => "String",
            IUInt32Property   => "UInt32",
            IUInt64Property   => "UInt64",
            IVectorProperty   => "Vector",
            IWaveFormProperty => "WaveForm",
            _ => "Unknown",
        };

        jo.AddFirst(new JProperty("propertyType", propertyType));
        jo.WriteTo(writer);
    }
}
