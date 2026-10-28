using Autodesk.Revit.DB;
using System;

public static class ElementIdExtensions
{
    private static readonly Func<ElementId, long> _getValue = BuildGetter();

    private static Func<ElementId, long> BuildGetter()
    {
        // Revit 2024+: property is called "Value" and returns long
        var valueProp = typeof(ElementId).GetProperty("Value");
        if (valueProp != null)
            return id => (long)valueProp.GetValue(id);

        // Revit 2022-2023: property is called "IntegerValue" and returns int
        var intProp = typeof(ElementId).GetProperty("IntegerValue");
        if (intProp != null)
            return id => (long)(int)intProp.GetValue(id);

        throw new InvalidOperationException(
            "Cannot find Value or IntegerValue on ElementId. Unknown Revit version.");
    }

    public static long GetValue(this ElementId id) => _getValue(id);

    /// <summary>ElementId from a stored value: ElementId(long) only exists from Revit 2024 on.</summary>
#if NETFRAMEWORK
    public static ElementId FromValue(long value) => new ElementId((int)value);
#else
    public static ElementId FromValue(long value) => new ElementId(value);
#endif
}
