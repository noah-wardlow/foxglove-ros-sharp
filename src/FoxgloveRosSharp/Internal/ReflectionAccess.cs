using System;
using System.Linq;
using System.Reflection;

namespace RosSharp.RosBridgeClient.Internal
{
    internal static class ReflectionAccess
    {
        public static object? GetMemberValue(object target, string name)
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.GetValue(target);

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(target);
        }

        public static Type? GetMemberType(Type targetType, string name)
        {
            PropertyInfo? property = targetType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.PropertyType;

            FieldInfo? field = targetType.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.FieldType;
        }

        public static void SetMemberValue(object target, string name, object? value)
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertValue(value, property.PropertyType));
                return;
            }

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            field?.SetValue(target, ConvertValue(value, field.FieldType));
        }

        public static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            Type nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableTarget.IsInstanceOfType(value))
                return value;

            if (nonNullableTarget.IsEnum)
                return Enum.ToObject(nonNullableTarget, value);

            return Convert.ChangeType(value, nonNullableTarget);
        }

        public static Array ToTypedArray(object?[] values, Type arrayType)
        {
            Type elementType = arrayType.GetElementType() ?? typeof(object);
            Array array = Array.CreateInstance(elementType, values.Length);
            for (int i = 0; i < values.Length; i++)
                array.SetValue(ConvertValue(values[i], elementType), i);
            return array;
        }

        public static Type FindMessageType(string rosType, Type preferredAssemblyType)
        {
            string simpleName = rosType[(rosType.LastIndexOf('/') + 1)..];
            Type? match = preferredAssemblyType.Assembly.GetTypes()
                .FirstOrDefault(t => typeof(Message).IsAssignableFrom(t) &&
                                     string.Equals(t.Name, simpleName, StringComparison.Ordinal));

            if (match != null)
                return match;

            return typeof(DynamicMessage);
        }
    }

    internal sealed class DynamicMessage : Message { }
}
