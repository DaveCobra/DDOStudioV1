using System;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi;

public static class Extensions
{
    public static bool IsValid(this string input, out uint parsedId, out IActionResult error)
    {
        parsedId = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = new BadRequestResult();
            return false;
        }

        if (input.StartsWith("0x"))
        {
            if (!uint.TryParse(input.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsedId))
            {
                error = new BadRequestObjectResult("Could not parse the ID provided.");
                return false;
            }
        }
        else if (!uint.TryParse(input, out parsedId))
        {
            error = new BadRequestObjectResult("Could not parse the ID provided.");
            return false;
        }

        return true;
    }

    public static T GetAttributeOfType<T>(this Enum enumVal) where T : System.Attribute
    {
        var type = enumVal.GetType();
        var memInfo = type.GetMember(enumVal.ToString());
        var attributes = memInfo[0].GetCustomAttributes(typeof(T), false);
        return (attributes.Length > 0) ? (T)attributes[0] : null;
    }

    public static uint GetWeenieType(this IPropertyCollection propertyCollection)
    {
        if (propertyCollection == null) return 0;
        var prop = propertyCollection.GetEnumProperty((uint)DdoProperty.WeenieType);
        return prop?.UInt32Value ?? 0;
    }
}
